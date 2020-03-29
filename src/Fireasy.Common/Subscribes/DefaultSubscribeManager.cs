﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Fireasy.Common.Subscribes
{
    /// <summary>
    /// 默认的订阅管理器。
    /// </summary>
    public class DefaultSubscribeManager : ISubscribeManager
    {
        private readonly SubscriberCollection subscribers = new SubscriberCollection();
        private readonly ConcurrentQueue<SubjectData> queue = new ConcurrentQueue<SubjectData>();
        private readonly Timer timer = null;
        private bool processing = false;

        private class SubjectData
        {
            public SubjectData(Type type, object data)
            {
                Name = TopicHelper.GetTopicName(type);
                Data = data;
            }

            public SubjectData(string name, object data)
            {
                Name = name;
                Data = data;
            }

            public string Name { get; set; }

            public object Data { get; set; }
        }

        /// <summary>
        /// 缺省实例。
        /// </summary>
        public static ISubscribeManager Instance = new DefaultSubscribeManager();

        protected DefaultSubscribeManager()
        {
            timer = new Timer(o => ProcessQueue(), null, 10, 1000);
        }

        private void ProcessQueue()
        {
            if (processing)
            {
                return;
            }

            processing = true;
            while (queue.TryDequeue(out SubjectData obj) && obj != null)
            {
                Tracer.Debug($"DefaultSubscribeManager accept message of '{obj.Name}'.");
                subscribers.Accept(obj.Name, obj.Data);
            }
            processing = false;
        }

        /// <summary>
        /// 向管理器发送主题。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subject">主题内容。</param>
        public void Publish<TSubject>(TSubject subject) where TSubject : class
        {
            queue.Enqueue(new SubjectData(typeof(TSubject), subject));
        }

        /// <summary>
        /// 向管理器发送主题。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="name">主题名称。</param>
        /// <param name="subject">主题内容。</param>
        public void Publish<TSubject>(string name, TSubject subject) where TSubject : class
        {
            queue.Enqueue(new SubjectData(name, subject));
        }

        /// <summary>
        /// 异步方式向管理器发送主题。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subject">主题内容。</param>
        /// <param name="cancellationToken">取消操作的通知。</param>
        public async Task PublishAsync<TSubject>(TSubject subject, CancellationToken cancellationToken = default) where TSubject : class
        {
            await Task.Run(() => Publish(subject));
        }

        /// <summary>
        /// 异步方式向管理器发送主题。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="name">主题名称。</param>
        /// <param name="subject">主题内容。</param>
        /// <param name="cancellationToken">取消操作的通知。</param>
        public async Task PublishAsync<TSubject>(string name, TSubject subject, CancellationToken cancellationToken = default) where TSubject : class
        {
            await Task.Run(() => Publish(name, subject));
        }

        /// <summary>
        /// 添加一个订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddSubscriber<TSubject>(Action<TSubject> subscriber) where TSubject : class
        {
            Guard.ArgumentNull(subscriber, nameof(subscriber));

            subscribers.AddSyncSubscriber(typeof(TSubject), TopicHelper.GetTopicName(typeof(TSubject)), subscriber);
        }

        /// <summary>
        /// 添加一个异步的订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddAsyncSubscriber<TSubject>(Func<TSubject, Task> subscriber) where TSubject : class
        {
            Guard.ArgumentNull(subscriber, nameof(subscriber));

            subscribers.AddAsyncSubscriber(typeof(TSubject), TopicHelper.GetTopicName(typeof(TSubject)), subscriber);
        }

        /// <summary>
        /// 添加一个订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="name">主题名称。</param>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddSubscriber<TSubject>(string name, Action<TSubject> subscriber) where TSubject : class
        {
            Guard.ArgumentNull(subscriber, nameof(subscriber));

            subscribers.AddSyncSubscriber(typeof(TSubject), name, subscriber);
        }

        /// <summary>
        /// 添加一个异步的订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="name">主题名称。</param>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddAsyncSubscriber<TSubject>(string name, Func<TSubject, Task> subscriber) where TSubject : class
        {
            Guard.ArgumentNull(subscriber, nameof(subscriber));

            subscribers.AddAsyncSubscriber(typeof(TSubject), name, subscriber);
        }

        /// <summary>
        /// 添加一个订阅方法。
        /// </summary>
        /// <param name="subjectType">主题的类型。</param>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddSubscriber(Type subjectType, Delegate subscriber)
        {
            Guard.ArgumentNull(subscriber, nameof(subscriber));

            subscribers.AddSyncSubscriber(subjectType, TopicHelper.GetTopicName(subjectType), subscriber);
        }

        /// <summary>
        /// 移除相关的订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        public void RemoveSubscriber<TSubject>()
        {
            RemoveSubscriber(typeof(TSubject));
        }

        /// <summary>
        /// 移除相关的订阅方法。
        /// </summary>
        /// <param name="subjectType">主题的类型。</param>
        public void RemoveSubscriber(Type subjectType)
        {
            subscribers.Remove(TopicHelper.GetTopicName(subjectType));
        }

        void ISubscribeManager.Publish(string name, byte[] data)
        {
            throw new NotImplementedException();
        }

        Task ISubscribeManager.PublishAsync(string name, byte[] data, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        void ISubscribeManager.AddSubscriber(string name, Action<byte[]> subscriber)
        {
            throw new NotImplementedException();
        }

        void ISubscribeManager.RemoveSubscriber(string name)
        {
            throw new NotImplementedException();
        }
    }
}
