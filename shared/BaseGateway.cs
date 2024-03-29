﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Moonlight.Shared.Internal.Diagnostics;
using Moonlight.Shared.Internal.Events.Message;
using Moonlight.Shared.Internal.Events.Payload;
using Moonlight.Shared.Internal.Events.Response;
using Moonlight.Shared.Internal.Extensions;
using Moonlight.Shared.Internal.Json;

namespace Moonlight.Shared.Internal.Events
{
    // TODO: Concurrency, block a request simliar to a already processed one unless tagged with the [Concurrent] method attribute to combat force spamming events to achieve some kind of bug.
    [PublicAPI]
    public abstract class BaseGateway
    {
        protected abstract BaseLogger Logger { get; }

        private List<WaitingEvent> _queue = new List<WaitingEvent>();
        private List<EventSubscription> _subscriptions = new List<EventSubscription>();

        public async Task ProcessInboundAsync(EventMessage message, ISource source)
        {
            object InvokeDelegate(EventSubscription subscription)
            {
                var arguments = new List<object>();
                var @delegate = subscription.Delegate;
                var method = @delegate.Method;
                var takesSource = method.GetParameters().Any(self => self.ParameterType == source.GetType());
                var startingIndex = takesSource ? 1 : 0;

                if (takesSource)
                {
                    arguments.Add(source);
                }

                if (message.Parameters != null)
                {
                    var array = message.Parameters.FromJson<EventParameter[]>();
                    var holder = new List<object>();
                    var parameterInfos = @delegate.Method.GetParameters();

                    for (var index = 0; index < array.Length; index++)
                    {
                        var argument = array[index];
                        var type = parameterInfos[startingIndex + index].ParameterType;

                        holder.Add(argument.Deserialize(type));
                    }

                    arguments.AddRange(holder.ToArray());
                }

                return @delegate.DynamicInvoke(arguments.ToArray());
            }

            if (message.MethodType == EventMethodType.Get)
            {
                var subscription = _subscriptions.SingleOrDefault(self => self.Endpoint == message.Endpoint) ??
                                   throw new Exception($"Could not find handler for endpoint: {message.Endpoint}");
                var result = InvokeDelegate(subscription);
                var type = result.GetType();

                if (type.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var task = (Task)result;

                    using (var token = new CancellationTokenSource())
                    {
                        var delay = Task.Run(async () => { await GetDelayedTask(30000); }, token.Token);
                        var completed = await Task.WhenAny(task, delay);

                        if (completed == task)
                        {
                            token.Cancel();

                            await task.ConfigureAwait(false);

                            result = (object)((dynamic)task).Result;
                        }
                        else
                        {
                            throw new TimeoutException(
                                $"({message.Endpoint} - {subscription.Delegate.Method.DeclaringType?.Name ?? "null"}/{subscription.Delegate.Method.Name}) The operation has timed out.");
                        }
                    }
                }

                var response = new EventResponseMessage(message.Id, null, result.ToJson());

                TriggerImpl(EventConstant.OutboundPipeline, source.Handle, response);
            }
            else
            {
                _subscriptions.Where(self => self.Endpoint == message.Endpoint).ForEach(self => InvokeDelegate(self));
            }
        }

        public void ProcessOutbound(EventResponseMessage response)
        {
            var waiting = _queue.SingleOrDefault(self => self.Message.Id == response.Id) ?? throw new Exception($"No matching request was found: {response.Id}");

            _queue.Remove(waiting);
            waiting.Callback.Invoke(response.Serialized);
        }

        protected void SendInternal(int target, string endpoint, params object[] args)
        {
            var container = new EventMessage(endpoint, EventMethodType.Post, args);

            TriggerImpl(EventConstant.InboundPipeline, target, container);
        }

        protected async Task<T> GetInternal<T>(int target, string endpoint, params object[] args)
        {
            var message = new EventMessage(endpoint, EventMethodType.Get, args);
            var token = new CancellationTokenSource();
            var holder = new EventValueHolder<T>();

            TriggerImpl(EventConstant.InboundPipeline, target, message);
            _queue.Add(new WaitingEvent(message, response =>
            {
                holder.Value = response.FromJson<T>();
                token.Cancel();
            }));

            while (!token.IsCancellationRequested)
            {
                await GetDelayedTask(1);
            }

            return holder.Value;
        }

        public void Mount(string endpoint, Delegate @delegate)
        {
            Logger.Debug($"Mounted: {endpoint}");

             _subscriptions.Add(new EventSubscription(endpoint, @delegate));
        }

        protected abstract Task GetDelayedTask(int milliseconds = 0);
        protected abstract void TriggerImpl(string pipeline, int target, ISerializable payload);
    }
}
