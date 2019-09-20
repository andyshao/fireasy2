﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using Fireasy.Common.ComponentModel;
using System.Collections.Generic;
using CSRedis;
using Fireasy.Common.Configuration;
using Fireasy.Common.Subscribes;
using System;
using System.Text;

namespace Fireasy.Redis
{
    /// <summary>
    /// 基于 Redis 的消息订阅管理器。
    /// </summary>
    [ConfigurationSetting(typeof(RedisConfigurationSetting))]
    public class SubscribeManager : RedisComponent, ISubscribeManager
    {
        private SafetyDictionary<string, List<CSRedisClient.SubscribeObject>> channels = new SafetyDictionary<string, List<CSRedisClient.SubscribeObject>>();

        /// <summary>
        /// 向 Redis 服务器发送消息主题。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subject">主题内容。</param>
        public void Publish<TSubject>(TSubject subject) where TSubject : class
        {
            var client = GetConnection();
            var channelName = ChannelHelper.GetChannelName(typeof(TSubject));
            client.Publish(channelName, Serialize(subject));
        }

        /// <summary>
        /// 向指定的 Redis 通道发送数据。
        /// </summary>
        /// <param name="channel">通道名称。</param>
        /// <param name="data">发送的数据。</param>
        public void Publish(string channel, byte[] data)
        {
            var client = GetConnection();
            client.Publish(channel, Encoding.UTF8.GetString(data));
        }

        /// <summary>
        /// 在 Redis 服务器中添加一个订阅方法。
        /// </summary>
        /// <typeparam name="TSubject"></typeparam>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddSubscriber<TSubject>(Action<TSubject> subscriber) where TSubject : class
        {
            var client = GetConnection();
            var channelName = ChannelHelper.GetChannelName(typeof(TSubject));
            channels.GetOrAdd(channelName, () => new List<CSRedisClient.SubscribeObject>())
                .Add(client.Subscribe((channelName, msg =>
                {
                    var subject = Deserialize<TSubject>(msg.Body);
                    subscriber(subject);
                }
            )));
        }

        /// <summary>
        /// 在 Redis 服务器中添加一个订阅方法。
        /// </summary>
        /// <param name="subjectType">主题的类型。</param>
        /// <param name="subscriber">读取主题的方法。</param>
        public void AddSubscriber(Type subjectType, Delegate subscriber)
        {
            var client = GetConnection();
            var channelName = ChannelHelper.GetChannelName(subjectType);
            channels.GetOrAdd(channelName, () => new List<CSRedisClient.SubscribeObject>())
                .Add(client.Subscribe((channelName, msg =>
                {
                    var subject = Deserialize(subjectType, msg.Body);
                    if (subject != null)
                    {
                        subscriber.DynamicInvoke(subject);
                    }
                }
            )));
        }

        /// <summary>
        /// 在 Redis 服务器中添加一个订阅方法。
        /// </summary>
        /// <param name="channel">通道名称。</param>
        /// <param name="subscriber">读取数据的方法。</param>
        public void AddSubscriber(string channel, Action<byte[]> subscriber)
        {
            var client = GetConnection();
            channels.GetOrAdd(channel, () => new List<CSRedisClient.SubscribeObject>())
                .Add(client.Subscribe((channel, msg =>
                {
                    subscriber.DynamicInvoke(Encoding.UTF8.GetBytes(msg.Body));
                }
            )));
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
            var client = GetConnection();
            var channelName = ChannelHelper.GetChannelName(subjectType);
            RemoveSubscriber(channelName);
        }

        /// <summary>
        /// 移除指定通道的订阅方法。
        /// </summary>
        /// <param name="channel">通道名称。</param>
        public void RemoveSubscriber(string channel)
        {
            var client = GetConnection();
            if (channels.TryRemove(channel, out List<CSRedisClient.SubscribeObject> subs) && subs != null)
            {
                subs.ForEach(s => s.Dispose());
            }
        }
    }
}
