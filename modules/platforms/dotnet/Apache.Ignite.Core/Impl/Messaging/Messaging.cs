/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Apache.Ignite.Core.Impl.Messaging
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Apache.Ignite.Core.Cluster;
    using Apache.Ignite.Core.Impl.Binary;
    using Apache.Ignite.Core.Impl.Collections;
    using Apache.Ignite.Core.Impl.Common;
    using Apache.Ignite.Core.Impl.Resource;
    using Apache.Ignite.Core.Impl.Unmanaged;
    using Apache.Ignite.Core.Messaging;
    using UU = Apache.Ignite.Core.Impl.Unmanaged.UnmanagedUtils;

    /// <summary>
    /// Messaging functionality.
    /// </summary>
    internal class Messaging : PlatformTarget, IMessaging
    {
        /// <summary>
        /// Opcodes.
        /// </summary>
        private enum Op
        {
            LocalListen = 1,
            RemoteListen = 2,
            Send = 3,
            SendMulti = 4,
            SendOrdered = 5,
            StopLocalListen = 6,
            StopRemoteListen = 7
        }

        /** Map from user (func+topic) -> id, needed for unsubscription. */
        private readonly MultiValueDictionary<KeyValuePair<object, object>, long> _funcMap =
            new MultiValueDictionary<KeyValuePair<object, object>, long>();

        /** Grid */
        private readonly Ignite _ignite;
        
        /** Async instance. */
        private readonly Lazy<Messaging> _asyncInstance;

        /** Async flag. */
        private readonly bool _isAsync;

        /** Cluster group. */
        private readonly IClusterGroup _clusterGroup;

        /// <summary>
        /// Initializes a new instance of the <see cref="Messaging" /> class.
        /// </summary>
        /// <param name="target">Target.</param>
        /// <param name="marsh">Marshaller.</param>
        /// <param name="prj">Cluster group.</param>
        public Messaging(IUnmanagedTarget target, Marshaller marsh, IClusterGroup prj)
            : base(target, marsh)
        {
            Debug.Assert(prj != null);

            _clusterGroup = prj;

            _ignite = (Ignite) prj.Ignite;

            _asyncInstance = new Lazy<Messaging>(() => new Messaging(this));
        }

        /// <summary>
        /// Initializes a new async instance.
        /// </summary>
        /// <param name="messaging">The messaging.</param>
        private Messaging(Messaging messaging) : base(UU.MessagingWithASync(messaging.Target), messaging.Marshaller)
        {
            _isAsync = true;
            _ignite = messaging._ignite;
            _clusterGroup = messaging.ClusterGroup;
        }

        /** <inheritdoc /> */
        public IClusterGroup ClusterGroup
        {
            get { return _clusterGroup; }
        }

        /// <summary>
        /// Gets the asynchronous instance.
        /// </summary>
        private Messaging AsyncInstance
        {
            get { return _asyncInstance.Value; }
        }

        /** <inheritdoc /> */

        public void Send(object message, object topic = null)
        {
            IgniteArgumentCheck.NotNull(message, "message");

            DoOutOp((int) Op.Send, topic, message);
        }

        /** <inheritdoc /> */
        public void SendAll(IEnumerable messages, object topic = null)
        {
            IgniteArgumentCheck.NotNull(messages, "messages");

            DoOutOp((int) Op.SendMulti, writer =>
            {
                writer.Write(topic);

                WriteEnumerable(writer, messages.OfType<object>());
            });
        }

        /** <inheritdoc /> */
        public void SendOrdered(object message, object topic = null, TimeSpan? timeout = null)
        {
            IgniteArgumentCheck.NotNull(message, "message");

            DoOutOp((int) Op.SendOrdered, writer =>
            {
                writer.Write(topic);
                writer.Write(message);

                writer.WriteLong((long)(timeout == null ? 0 : timeout.Value.TotalMilliseconds));
            });
        }

        /** <inheritdoc /> */
        public void LocalListen<T>(IMessageListener<T> listener, object topic = null)
        {
            IgniteArgumentCheck.NotNull(listener, "filter");

            ResourceProcessor.Inject(listener, _ignite);

            lock (_funcMap)
            {
                var key = GetKey(listener, topic);

                MessageListenerHolder filter0 = MessageListenerHolder.CreateLocal(_ignite, listener); 

                var filterHnd = _ignite.HandleRegistry.Allocate(filter0);

                filter0.DestroyAction = () =>
                {
                    lock (_funcMap)
                    {
                        _funcMap.Remove(key, filterHnd);
                    }
                };

                try
                {
                    DoOutOp((int) Op.LocalListen, writer =>
                    {
                        writer.WriteLong(filterHnd);
                        writer.Write(topic);
                    });
                }
                catch (Exception)
                {
                    _ignite.HandleRegistry.Release(filterHnd);

                    throw;
                }

                _funcMap.Add(key, filterHnd);
            }
        }

        /** <inheritdoc /> */
        public void StopLocalListen<T>(IMessageListener<T> listener, object topic = null)
        {
            IgniteArgumentCheck.NotNull(listener, "filter");

            long filterHnd;
            bool removed;

            lock (_funcMap)
            {
                removed = _funcMap.TryRemove(GetKey(listener, topic), out filterHnd);
            }

            if (removed)
            {
                DoOutOp((int) Op.StopLocalListen, writer =>
                {
                    writer.WriteLong(filterHnd);
                    writer.Write(topic);
                });
            }
        }

        /** <inheritdoc /> */
        public Guid RemoteListen<T>(IMessageListener<T> listener, object topic = null)
        {
            IgniteArgumentCheck.NotNull(listener, "filter");

            var filter0 = MessageListenerHolder.CreateLocal(_ignite, listener);
            var filterHnd = _ignite.HandleRegistry.AllocateSafe(filter0);

            try
            {
                Guid id = Guid.Empty;

                DoOutInOp((int) Op.RemoteListen,
                    writer =>
                    {
                        writer.Write(filter0);
                        writer.WriteLong(filterHnd);
                        writer.Write(topic);
                    },
                    input =>
                    {
                        var id0 = Marshaller.StartUnmarshal(input).GetRawReader().ReadGuid();

                        Debug.Assert(_isAsync || id0.HasValue);

                        if (id0.HasValue)
                            id = id0.Value;
                    });

                return id;
            }
            catch (Exception)
            {
                _ignite.HandleRegistry.Release(filterHnd);

                throw;
            }
        }

        /** <inheritdoc /> */
        public Task<Guid> RemoteListenAsync<T>(IMessageListener<T> listener, object topic = null)
        {
            AsyncInstance.RemoteListen(listener, topic);

            return AsyncInstance.GetTask<Guid>();
        }

        /** <inheritdoc /> */
        public void StopRemoteListen(Guid opId)
        {
            DoOutOp((int) Op.StopRemoteListen, writer =>
            {
                writer.WriteGuid(opId);
            });
        }

        /** <inheritdoc /> */
        public Task StopRemoteListenAsync(Guid opId)
        {
            AsyncInstance.StopRemoteListen(opId);

            return AsyncInstance.GetTask();
        }

        /// <summary>
        /// Gets the key for user-provided filter and topic.
        /// </summary>
        /// <param name="filter">Filter.</param>
        /// <param name="topic">Topic.</param>
        /// <returns>Compound dictionary key.</returns>
        private static KeyValuePair<object, object> GetKey(object filter, object topic)
        {
            return new KeyValuePair<object, object>(filter, topic);
        }
    }
}