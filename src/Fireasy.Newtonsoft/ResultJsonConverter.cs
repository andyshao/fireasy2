﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using Fireasy.Common.ComponentModel;
using System;
using Newton = Newtonsoft.Json;

namespace Fireasy.Newtonsoft
{
    /// <summary>
    /// 为 JSON.NET 提供 <see cref="IResult"/> 类型的转换器。
    /// </summary>
    public class ResultJsonConverter : Newton.JsonConverter
    {
        /// <summary>
        /// 判断对象类型是不是实现自 <see cref="IResult"/> 接口。
        /// </summary>
        /// <param name="objectType"></param>
        /// <returns></returns>
        public override bool CanConvert(Type objectType)
        {
            return typeof(IResult).IsAssignableFrom(objectType);
        }

        /// <summary>
        /// 不支持反序列化。
        /// </summary>
        public override bool CanRead => false;

        public override void WriteJson(Newton.JsonWriter writer, object value, Newton.JsonSerializer serializer)
        {
            var result = value as IResult;

            writer.WriteStartObject();

            writer.WritePropertyName("succeed");
            serializer.Serialize(writer, result.Succeed);

            writer.WritePropertyName("data");
            serializer.Serialize(writer, result.Data);

            if (!string.IsNullOrEmpty(result.Message))
            {
                writer.WritePropertyName("msg");
                serializer.Serialize(writer, result.Message);
            }

            writer.WriteEndObject();
        }

        public override object ReadJson(Newton.JsonReader reader, Type objectType, object existingValue, Newton.JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
}
