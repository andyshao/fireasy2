﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using Fireasy.Common;
using Fireasy.Common.Configuration;
using Fireasy.Common.Extensions;
using Fireasy.Common.ComponentModel;
using CSRedis;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Fireasy.Common.Serialization;
using Fireasy.Common.Threading;

namespace Fireasy.Redis
{
    /// <summary>
    /// Redis 组件抽象类。
    /// </summary>
    public abstract class RedisComponent : DisposeableBase, IConfigurationSettingHostService, IServiceProviderAccessor
    {
        private List<int> dbRanage;
        private Func<string, string> captureRule;
        private Func<ISerializer> serializerFactory;
        private readonly List<string> connectionStrs = new List<string>();
        private readonly SafetyDictionary<int, CSRedisClient> clients = new SafetyDictionary<int, CSRedisClient>();

        /// <summary>
        /// 获取或设置应用程序服务提供者实例。
        /// </summary>
        public IServiceProvider ServiceProvider { get; set; }

        /// <summary>
        /// 获取 Redis 的相关配置。
        /// </summary>
        protected RedisConfigurationSetting Setting { get; private set; }

        /// <summary>
        /// 序列化对象。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual T Deserialize<T>(string value)
        {
            var serializer = GetSerializer();
            if (serializer is ITextSerializer txtSerializer)
            {
                return txtSerializer.Deserialize<T>(value);
            }

            return serializer.Deserialize<T>(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// 序列化对象。
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual object Deserialize(Type type, string value)
        {
            var serializer = GetSerializer();
            if (serializer is ITextSerializer txtSerializer)
            {
                return txtSerializer.Deserialize(value, type);
            }

            return serializer.Deserialize(Encoding.UTF8.GetBytes(value), type);
        }

        /// <summary>
        /// 反序列化。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual string Serialize<T>(T value)
        {
            var serializer = GetSerializer();
            if (serializer is ITextSerializer txtSerializer)
            {
                return txtSerializer.Serialize(value);
            }

            return Encoding.UTF8.GetString(serializer.Serialize(value));
        }

        protected virtual CSRedisClient CreateClient(string[] constrs)
        {
            return new CSRedisClient(null, constrs);
        }

        protected CSRedisClient GetConnection(string key = null)
        {
            if (string.IsNullOrEmpty(key) || dbRanage == null)
            {
                return clients[0] ?? throw new InvalidOperationException("不能初始化 Redis 的连接。");
            }

            var ckey = captureRule == null ? key : captureRule(key);
            var index = GetModulus(ckey, dbRanage.Count);

            Tracer.Debug($"Select redis db{dbRanage[index]} for the key '{key}'.");

            return clients.GetOrAdd(index, k =>
            {
                var constrs = connectionStrs.Select(s => string.Concat(s, $",defaultDatabase={dbRanage[k]}")).ToArray();
                constrs.ForEach(s =>
                {
                    Tracer.Debug($"Connecting to the redis server '{s}'.");
                });
                return CreateClient(constrs);
            });
        }

        protected IEnumerable<CSRedisClient> GetConnections()
        {
            return clients.Values;
        }


        void IConfigurationSettingHostService.Attach(IConfigurationSettingItem setting)
        {
            if (Setting != null)
            {
                return;
            }

            Setting = (RedisConfigurationSetting)setting;

            if (!string.IsNullOrEmpty(Setting.ConnectionString))
            {
                connectionStrs.Add(Setting.ConnectionString);
            }
            else
            {
                foreach (var host in Setting.Hosts)
                {
                    var connStr = new StringBuilder($"{host.Server}");

            #region connection build
                    if (host.Port != 0)
                    {
                        connStr.Append($":{host.Port}");
                    }

                    if (!string.IsNullOrEmpty(Setting.Password))
                    {
                        connStr.Append($",password={Setting.Password}");
                    }

                    if (Setting.DefaultDb != 0 && string.IsNullOrEmpty(Setting.DbRange))
                    {
                        connStr.Append($",defaultDatabase={Setting.DefaultDb}");
                    }

                    if (Setting.Ssl)
                    {
                        connStr.Append($",ssl=true");
                    }

                    if (Setting.WriteBuffer != null)
                    {
                        connStr.Append($",writeBuffer={Setting.WriteBuffer}");
                    }

                    if (Setting.PoolSize != null)
                    {
                        connStr.Append($",poolsize={Setting.PoolSize}");
                    }

                    if (Setting.ConnectTimeout.Milliseconds != 5000)
                    {
                        connStr.Append($",connectTimeout={Setting.ConnectTimeout.Milliseconds}");
                    }

                    if (Setting.SyncTimeout.Milliseconds != 10000)
                    {
                        connStr.Append($",syncTimeout={Setting.SyncTimeout.Milliseconds}");
                    }

                    connStr.Append(",allowAdmin=true");
            #endregion

                    connectionStrs.Add(connStr.ToString());
                }
            }

            if (string.IsNullOrEmpty(Setting.DbRange))
            {
                clients.GetOrAdd(0, () => new CSRedisClient(null, connectionStrs.ToArray()));
            }
            else
            {
                ParseDbRange(Setting.DbRange);
                if (!string.IsNullOrEmpty(Setting.KeyRule))
                {
                    ParseKeyCapture(Setting.KeyRule);
                }
            }
        }

        IConfigurationSettingItem IConfigurationSettingHostService.GetSetting()
        {
            return Setting;
        }

        private int GetModulus(string str, int mod)
        {
            var hash = 0;
            foreach (var c in str)
            {
                hash = (128 * hash % (mod * 8)) + c;
            }

            return hash % mod;
        }

        private void ParseDbRange(string range)
        {
            dbRanage = new List<int>();
            foreach (var s1 in range.Split(','))
            {
                int p;
                if ((p = s1.IndexOf('-')) != -1)
                {
                    var start = s1.Substring(0, p).To<int>();
                    var end = s1.Substring(p + 1).To<int>();
                    for (var i = start; i <= end; i++)
                    {
                        dbRanage.Add(i);
                    }
                }
                else
                {
                    dbRanage.Add(s1.To<int>());
                }
            }
        }

        private void ParseKeyCapture(string rule)
        {
            if (rule.StartsWith("left", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+)");
                var length = match.Groups[1].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Left(length));
            }
            if (rule.StartsWith("right", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+)");
                var length = match.Groups[1].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Right(length));
            }
            else if (rule.StartsWith("substr", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+),\s*(\d+)");
                var start = match.Groups[1].Value.To<int>();
                var length = match.Groups[2].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Substring(start, start + length > k.Length ? k.Length - start : length));
            }
        }

        private ISerializer GetSerializer()
        {
            return SingletonLocker.Lock(ref serializerFactory, this, () =>
            {
                return new Func<ISerializer>(() =>
                {
                    ISerializer _serializer = null;
                    if (Setting.SerializerType != null)
                    {
                        _serializer = Setting.SerializerType.New<ISerializer>();
                    }

                    if (_serializer == null)
                    {
                        _serializer = ServiceProvider.TryGetService<ISerializer>(() => SerializerFactory.CreateSerializer());
                    }

                    if (_serializer == null)
                    {
                        var option = new JsonSerializeOption();
                        option.Converters.Add(new FullDateTimeJsonConverter());
                        _serializer = new JsonSerializer(option);
                    }

                    return _serializer;
                });
            })();
        }

        protected override bool Dispose(bool disposing)
        {
            foreach (var client in clients)
            {
                client.Value.Dispose();
            }

            clients.Clear();
            return base.Dispose(disposing);
        }
    }
}
