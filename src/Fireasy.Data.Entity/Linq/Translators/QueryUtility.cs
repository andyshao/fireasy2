﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// This source code is made available under the terms of the Microsoft Public License (MS-PL)

using Fireasy.Common.Extensions;
using Fireasy.Common.Reflection;
using Fireasy.Data.Converter;
using Fireasy.Data.Entity.Linq.Expressions;
using Fireasy.Data.Entity.Metadata;
using Fireasy.Data.Entity.Properties;
using Fireasy.Data.Extensions;
using Fireasy.Data.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;

namespace Fireasy.Data.Entity.Linq.Translators
{
    internal sealed class QueryUtility
    {
        internal static LambdaExpression GetAggregator(Type expectedType, Type actualType)
        {
            var actualElementType = ReflectionCache.GetMember("EnumerableElementType", actualType, k => k.GetEnumerableElementType());
            if (!expectedType.IsAssignableFrom(actualType))
            {
                var expectedElementType = ReflectionCache.GetMember("EnumerableElementType", expectedType, k => k.GetEnumerableElementType());
                var p = Expression.Parameter(actualType, "p");
                Expression body = null;

                if (expectedType.IsAssignableFrom(actualElementType))
                {
                    body = Expression.Call(typeof(Enumerable), nameof(Enumerable.SingleOrDefault), new[] { actualElementType }, p);
                }
                else if (expectedType.IsGenericType && expectedType.GetGenericTypeDefinition() == typeof(IQueryable<>))
                {
                    body = Expression.Call(typeof(Queryable), nameof(Queryable.AsQueryable), new[] { expectedElementType }, CastElementExpression(expectedElementType, p));
                }
                else if (expectedType.IsArray && expectedType.GetArrayRank() == 1)
                {
                    body = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { expectedElementType }, CastElementExpression(expectedElementType, p));
                }
                else if (expectedType.IsGenericType && expectedType.GetGenericTypeDefinition() == typeof(EntitySet<>))
                {
                    body = Expression.Call(typeof(EnumerableExtension), nameof(EnumerableExtension.ToEntitySet), new[] { expectedElementType }, CastElementExpression(expectedElementType, p));
                }
                else if (expectedType.IsAssignableFrom(typeof(List<>).MakeGenericType(actualElementType)))
                {
                    body = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { expectedElementType }, CastElementExpression(expectedElementType, p));
                }
                else
                {
                    var ci = expectedType.GetConstructor(new[] { actualType });
                    if (ci != null)
                    {
                        body = Expression.New(ci, p);
                    }
                }

                if (body != null)
                {
                    return Expression.Lambda(body, p);
                }
            }

            return null;
        }

        /// <summary>
        /// 判断表达式是否可用为 Column 使用。
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        internal static bool CanBeColumnExpression(Expression expression)
        {
            if (expression is ProjectionExpression proj && proj.IsSingleton && proj.Type.IsDbTypeSupported())
            {
                return true;
            }

            switch (expression.NodeType)
            {
                case (ExpressionType)DbExpressionType.Column:
                case (ExpressionType)DbExpressionType.Scalar:
                case (ExpressionType)DbExpressionType.Exists:
                case (ExpressionType)DbExpressionType.AggregateSubquery:
                case (ExpressionType)DbExpressionType.Aggregate:
                    return true;
                default:
                    return false;
            }
        }

        private static Expression CastElementExpression(Type expectedElementType, Expression expression)
        {
            var elementType = ReflectionCache.GetMember("EnumerableElementType", expression.Type, k => k.GetEnumerableElementType());
            if (expectedElementType != elementType &&
                (expectedElementType.IsAssignableFrom(elementType) ||
                  elementType.IsAssignableFrom(expectedElementType)))
            {
                return Expression.Call(typeof(Enumerable), nameof(Enumerable.Cast), new[] { expectedElementType }, expression);
            }

            return expression;
        }

        internal static ProjectionExpression GetTableQuery(TranslateContext transContext, EntityMetadata entity, bool isNoTracking, bool isAsync)
        {
            var tableAlias = new TableAlias();
            var selectAlias = new TableAlias();
            var entityType = entity.EntityType;
            var table = new TableExpression(tableAlias, entity.TableName, entityType);

            var projector = GetTypeProjection(transContext, table, entity);
            var pc = ColumnProjector.ProjectColumns(CanBeColumnExpression, projector, null, selectAlias, tableAlias);

            var proj = new ProjectionExpression(
                new SelectExpression(selectAlias, pc.Columns, table, null),
                pc.Projector, isAsync, isNoTracking
                );

            return (ProjectionExpression)transContext.QueryPolicy.ApplyPolicy(proj, entityType, ex => QueryBinder.Bind(transContext, ex));
        }

        internal static Expression GetTypeProjection(TranslateContext transContext, Expression root, EntityMetadata entity)
        {
            var entityType = entity.EntityType;

            //获取实体中定义的所有依赖属性
            var properties = PropertyUnity.GetLoadedProperties(entityType);

            var bindings = from p in properties
                           let mbrExp = GetMemberExpression(transContext, root, p)
                           where mbrExp != null
                           select Expression.Bind(p.Info.ReflectionInfo, mbrExp);

            return new EntityExpression(entity, Expression.MemberInit(Expression.New(entityType), bindings));
        }

        internal static Expression GetMemberExpression(TranslateContext transContext, Expression root, IProperty property)
        {
            if (property is RelationProperty relationProprety)
            {
                //所关联的实体类型
                var relMetadata = EntityMetadataUnity.GetEntityMetadata(relationProprety.RelationalType);
                var projection = GetTableQuery(transContext, relMetadata, false, false);

                Expression parentExp = null, childExp = null;
                var ship = RelationshipUnity.GetRelationship(relationProprety);

                if (ship.ThisType != relationProprety.EntityType)
                {
                    parentExp = projection.Projector;
                    childExp = root;
                }
                else
                {
                    parentExp = root;
                    childExp = projection.Projector;
                }

                var where = ship.Keys.Select(r =>
                        GetMemberExpression(transContext, parentExp, r.ThisProperty).Equal(GetMemberExpression(transContext, childExp, r.OtherProperty)))
                    .Aggregate(Expression.And);

                var newAlias = new TableAlias();
                var pc = ColumnProjector.ProjectColumns(CanBeColumnExpression, projection.Projector, null, newAlias, projection.Select.Alias);

                var aggregator = GetAggregator(property.Type, typeof(IEnumerable<>).MakeGenericType(pc.Projector.Type));
                var result = new ProjectionExpression(
                    new SelectExpression(newAlias, pc.Columns, projection.Select, where),
                    pc.Projector, aggregator, projection.IsAsync, projection.IsNoTracking
                    );

                return transContext.QueryPolicy.ApplyPolicy(result, property.Info.ReflectionInfo, ex => QueryBinder.Bind(transContext, ex));
            }

            if (root is TableExpression table)
            {
                if (property is SubqueryProperty sqProperty)
                {
                    return new SubqueryColumnExpression(property.Type, table.Alias, property.Info.FieldName, sqProperty.Subquery);
                }
                else if (property is ISavedProperty)
                {
                    return new ColumnExpression(property.Type, table.Alias, property.Name, property.Info);
                }
            }

            return QueryBinder.BindMember(root, property.Info.ReflectionInfo);
        }

        internal static Expression GetDeleteExpression(TranslateContext transContext, EntityMetadata metadata, LambdaExpression predicate, bool replace, bool isAsync)
        {
            var table = new TableExpression(new TableAlias(), metadata.TableName, metadata.EntityType);
            Expression where = null;

            if (predicate != null)
            {
                if (replace)
                {
                    var row = GetTypeProjection(transContext, table, metadata);
                    where = DbExpressionReplacer.Replace(predicate.Body, predicate.Parameters[0], row);
                }
                else
                {
                    where = predicate.Body;
                }
            }

            return new DeleteCommandExpression(table, where, isAsync);
        }

        internal static Expression GetLogicalDeleteExpression(TranslateContext transContext, EntityMetadata metadata, LambdaExpression predicate, bool isAsync)
        {
            var table = new TableExpression(new TableAlias(), metadata.TableName, metadata.EntityType);
            Expression where = null;

            var delflag = (ColumnExpression)GetMemberExpression(transContext, table, metadata.DeleteProperty);
            var assignments = new List<ColumnAssignment>
                {
                    new ColumnAssignment(delflag, Expression.Constant(true))
                };

            if (predicate != null)
            {
                var row = GetTypeProjection(transContext, table, metadata);
                where = DbExpressionReplacer.Replace(predicate.Body, predicate.Parameters[0], row);
            }

            return new UpdateCommandExpression(table, where, assignments, isAsync);
        }

        internal static Expression GetUpdateExpression(TranslateContext transContext, Expression instance, LambdaExpression predicate, bool isAsync)
        {
            if (instance is ParameterExpression)
            {
                return GetUpdateExpressionByParameter(transContext, (ParameterExpression)instance, predicate, isAsync);
            }
            else if (instance is ConstantExpression)
            {
                return GetUpdateExpressionByEntity(transContext, (ConstantExpression)instance, predicate, isAsync);
            }

            var lambda = instance as LambdaExpression;
            if (lambda == null && instance.NodeType == ExpressionType.Quote)
            {
                lambda = ((UnaryExpression)instance).Operand as LambdaExpression;
            }

            if (lambda != null)
            {
                return GetUpdateExpressionByCalculator(transContext, lambda, predicate, isAsync);
            }

            return null;
        }

        internal static LambdaExpression GetPrimaryKeyExpression(TranslateContext transContext, ParameterExpression parExp)
        {
            var metadata = EntityMetadataUnity.GetEntityMetadata(parExp.Type);
            var table = new TableExpression(new TableAlias(), metadata.TableName, parExp.Type);
            var primaryKeys = PropertyUnity.GetPrimaryProperties(parExp.Type);

            if (!primaryKeys.Any())
            {
                throw new NotSupportedException(SR.GetString(SRKind.NotDefinedPrimaryKey));
            }

            var where = primaryKeys.Select(p =>
                   GetMemberExpression(transContext, table, p).Equal(Expression.MakeMemberAccess(parExp, p.Info.ReflectionInfo)))
                .Aggregate(Expression.Add);

            return Expression.Lambda(where, parExp);
        }

        private static Expression GetUpdateExpressionByEntity(TranslateContext transContext, ConstantExpression constant, LambdaExpression predicate, bool isAsync)
        {
            var entity = constant.Value as IEntity;
            var metadata = EntityMetadataUnity.GetEntityMetadata(entity.EntityType);
            var table = new TableExpression(new TableAlias(), metadata.TableName, metadata.EntityType);
            Expression where = null;

            if (predicate != null)
            {
                var row = GetTypeProjection(transContext, table, metadata);
                where = DbExpressionReplacer.Replace(predicate.Body, predicate.Parameters[0], row);
            }

            return new UpdateCommandExpression(table, where, GetUpdateArguments(transContext, table, entity), isAsync);
        }

        private static Expression GetUpdateExpressionByParameter(TranslateContext transContext, ParameterExpression parExp, LambdaExpression predicate, bool isAsync)
        {
            var metadata = EntityMetadataUnity.GetEntityMetadata(parExp.Type);
            var table = new TableExpression(new TableAlias(), metadata.TableName, parExp.Type);

            return new UpdateCommandExpression(table, predicate.Body, GetUpdateArguments(transContext, table, parExp), isAsync);
        }

        private static Expression GetUpdateExpressionByCalculator(TranslateContext transContext, LambdaExpression lambda, LambdaExpression predicate, bool isAsync)
        {
            var initExp = lambda.Body as MemberInitExpression;
            var newExp = initExp.NewExpression;
            var entityType = newExp.Type;
            var metadata = EntityMetadataUnity.GetEntityMetadata(entityType);
            var table = new TableExpression(new TableAlias(), metadata.TableName, metadata.EntityType);
            Expression where = null;

            var row = GetTypeProjection(transContext, table, metadata);

            if (predicate != null)
            {
                where = DbExpressionReplacer.Replace(predicate.Body, predicate.Parameters[0], row);
            }

            var bindings = initExp.Bindings.Cast<MemberAssignment>().Select(m =>
                    Expression.Bind(m.Member, DbExpressionReplacer.Replace(m.Expression, lambda.Parameters[0], row)));

            return new UpdateCommandExpression(table, where, GetUpdateArguments(transContext, table, bindings), isAsync);
        }

        internal static Expression GetInsertExpression(TranslateContext transContext, Expression instance, bool isAsync)
        {
            var entityType = instance.Type;
            Func<TableExpression, Expression, IEnumerable<ColumnAssignment>> func;

            if (instance is ParameterExpression)
            {
                func = new Func<TableExpression, Expression, IEnumerable<ColumnAssignment>>((t, exp) => GetInsertArguments(transContext, t, exp.As<ParameterExpression>()));
            }
            else if (instance is ConstantExpression)
            {
                func = new Func<TableExpression, Expression, IEnumerable<ColumnAssignment>>((t, exp) => GetInsertArguments(transContext, t, exp.As<ConstantExpression>().Value as IEntity));
            }
            else
            {
                throw new ArgumentException("instance");
            }

            var metadata = EntityMetadataUnity.GetEntityMetadata(entityType);
            var table = new TableExpression(new TableAlias(), metadata.TableName, entityType);

            return new InsertCommandExpression(table, func(table, instance), isAsync)
            {
                WithAutoIncrement = !string.IsNullOrEmpty(transContext.SyntaxProvider.IdentitySelect) && HasAutoIncrement(instance.Type),
                WithGenerateValue = HasGenerateValue(instance.Type)
            };
        }

        internal static NamedValueExpression GetNamedValueExpression(string name, Expression value, DbType dbType)
        {
            var exp = value;
            if (exp.NodeType == ExpressionType.Convert)
            {
                exp = ((UnaryExpression)value).Operand;
            }

            if (exp.NodeType != ExpressionType.Constant)
            {
                return new NamedValueExpression(name, exp);
            }

            IValueConverter converter;
            if ((converter = ConvertManager.GetConverter(exp.Type)) != null)
            {
                exp = Expression.Constant(converter.ConvertTo(((ConstantExpression)exp).Value, dbType));
                exp = Expression.Convert(exp, typeof(object));
            }

            return new NamedValueExpression(name, exp, dbType);
        }

        private static bool HasAutoIncrement(Type entityType)
        {
            return PropertyUnity.GetPrimaryProperties(entityType).Any(s => s.Info.GenerateType == IdentityGenerateType.AutoIncrement);
        }

        private static bool HasGenerateValue(Type entityType)
        {
            return PropertyUnity.GetPrimaryProperties(entityType).Any(s => s.Info.GenerateType == IdentityGenerateType.Generator);
        }

        private static IEnumerable<ColumnAssignment> GetUpdateArguments(TranslateContext transContext, TableExpression table, IEntity entity)
        {
            var properties = GetModifiedProperties(entity).ToArray();

            return properties
                .Select(p => new ColumnAssignment(
                       (ColumnExpression)GetMemberExpression(transContext, table, p),
                       Expression.Constant(GetConvertableValue(entity, p))
                       )).ToList();
        }

        private static IEnumerable<ColumnAssignment> GetUpdateArguments(TranslateContext transContext, TableExpression table, ParameterExpression parExp)
        {
            IEnumerable<IProperty> properties = null;

            if (transContext.TemporaryBag != null)
            {
                properties = GetModifiedProperties(table.Type, transContext.TemporaryBag);
            }

            return from p in properties
                   select new ColumnAssignment(
                       (ColumnExpression)GetMemberExpression(transContext, table, p),
                       Expression.MakeMemberAccess(parExp, p.Info.ReflectionInfo));
        }

        private static IEnumerable<ColumnAssignment> GetUpdateArguments(TranslateContext transContext, TableExpression table, IEnumerable<MemberAssignment> bindings)
        {
            return from m in bindings
                   let property = PropertyUnity.GetProperty(table.Type, m.Member.Name)
                   where property != null
                   select new ColumnAssignment(
                       (ColumnExpression)GetMemberExpression(transContext, table, property),
                       m.Expression);
        }

        /// <summary>
        /// 返回插入具体实体时的 <see cref="ColumnAssignment"/> 集合。
        /// </summary>
        /// <param name="syntax"></param>
        /// <param name="table"></param>
        /// <param name="entity">具体的实体。</param>
        /// <returns></returns>
        private static IEnumerable<ColumnAssignment> GetInsertArguments(TranslateContext transContext, TableExpression table, IEntity entity)
        {
            var properties = GetModifiedProperties(entity);

            var assignments = properties
                .Select(p => new ColumnAssignment(
                       (ColumnExpression)GetMemberExpression(transContext, table, p),
                       Expression.Constant(GetConvertableValue(entity, p))
                       )).ToList();

            assignments.AddRange(GetAssignmentsForPrimaryKeys(transContext, table, null, entity));

            return assignments;
        }

        /// <summary>
        /// 返回插入具体实体时的 <see cref="ColumnAssignment"/> 集合。
        /// </summary>
        /// <param name="syntax"></param>
        /// <param name="table"></param>
        /// <param name="parExp"></param>
        /// <returns></returns>
        private static IEnumerable<ColumnAssignment> GetInsertArguments(TranslateContext transContext, TableExpression table, ParameterExpression parExp)
        {
            IEnumerable<IProperty> properties = null;

            if (transContext.TemporaryBag != null)
            {
                properties = GetModifiedProperties(table.Type, transContext.TemporaryBag);
            }

            var assignments = properties
                .Select(p => new ColumnAssignment(
                       (ColumnExpression)GetMemberExpression(transContext, table, p),
                       Expression.MakeMemberAccess(parExp, p.Info.ReflectionInfo)
                       )).ToList();

            assignments.AddRange(GetAssignmentsForPrimaryKeys(transContext, table, parExp, null));

            return assignments;
        }

        private static IEnumerable<ColumnAssignment> GetAssignmentsForPrimaryKeys(TranslateContext transContext, TableExpression table, ParameterExpression parExp, IEntity entity)
        {
            var entityType = parExp != null ? parExp.Type : entity.EntityType;
            var assignments = new List<ColumnAssignment>();

            foreach (var p in PropertyUnity.GetPrimaryProperties(entityType))
            {
                var pvExp = GetPrimaryValueExpression(transContext.SyntaxProvider, table, parExp, entity, p);

                if (pvExp != null)
                {
                    var columnExp = (ColumnExpression)GetMemberExpression(transContext, table, p);
                    assignments.Add(new ColumnAssignment(columnExp, pvExp));
                }
            }

            return assignments;
        }

        /// <summary>
        /// 获取实体可插入或更新的非主键属性列表。
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        private static IEnumerable<IProperty> GetModifiedProperties(IEntity entity)
        {
            var properties = PropertyUnity.GetPersistentProperties(entity.EntityType)
                .Where(p => !p.Info.IsPrimaryKey ||
                    (p.Info.IsPrimaryKey && p.Info.GenerateType == IdentityGenerateType.None));

            //判断实体类型有是不是编译的代理类型，如果不是，取非null的属性，否则使用IsModified判断
            var isNotCompiled = entity.GetType().IsNotCompiled();
            return properties.Where(p => isNotCompiled ?
                    !PropertyValue.IsEmpty(entity.GetValue(p)) :
                    entity.IsModified(p.Name));
        }

        /// <summary>
        /// 获取实体可插入或更新的非主键属性列表。
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="names"></param>
        /// <returns></returns>
        private static IEnumerable<IProperty> GetModifiedProperties(Type entityType, List<string> names)
        {
            return PropertyUnity.GetPersistentProperties(entityType)
                .Where(p => names.Contains(p.Name));
        }

        /// <summary>
        /// 获取主键值的表达式。
        /// </summary>
        /// <param name="syntax"></param>
        /// <param name="table"></param>
        /// <param name="parExp"></param>
        /// <param name="entity"></param>
        /// <param name="property"></param>
        /// <returns></returns>
        private static Expression GetPrimaryValueExpression(ISyntaxProvider syntax, TableExpression table, ParameterExpression parExp, IEntity entity, IProperty property)
        {
            //如果不支持自增量，则使用 IGenerateProvider生成
            if (property.Info.GenerateType == IdentityGenerateType.Generator ||
                (property.Info.GenerateType == IdentityGenerateType.AutoIncrement &&
                string.IsNullOrEmpty(syntax.IdentitySelect)))
            {
                return new GeneratorExpression(table, parExp ?? (Expression)Expression.Constant(entity), property);
            }

            return null;
        }

        /// <summary>
        /// 获取受 <see cref="IValueConverter"/> 支持的数据转换值。
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="property"></param>
        /// <returns></returns>
        private static object GetConvertableValue(IEntity entity, IProperty property)
        {
            var value = entity.GetValue(property);
            if (!PropertyValue.IsEmpty(value))
            {
                //查找属性类型对应的转换器
                var converter = ConvertManager.GetConverter(property.Type);
                if (converter != null && property.Info.DataType != null)
                {
                    var pvValue = value.GetValue();
                    return converter.ConvertTo(pvValue, (DbType)property.Info.DataType);
                }

                return value.GetValue();
            }

            return property.Type.GetDefaultValue();
        }

        /// <summary>
        /// 获取插入或更新所参照的实体。
        /// </summary>
        /// <param name="instances"></param>
        /// <param name="batchOpt"></param>
        /// <returns></returns>
        internal static List<string> GetModifiedProperties(Expression instances, BatchOperateOptions batchOpt)
        {
            if (!(instances is ConstantExpression consExp) || !(consExp.Value is IEnumerable entities))
            {
                return null;
            }

            var maxProperties = new List<string>();
            Type entityType = null;
            IEnumerable<IProperty> properties = null;

            //循环列表查找属性修改最多的那个实体作为参照
            foreach (IEntity entity in entities)
            {
                if (entityType == null)
                {
                    entityType = entity.GetType();
                    properties = PropertyUnity.GetPersistentProperties(entityType)
                        .Where(p => !p.Info.IsPrimaryKey ||
                            (p.Info.IsPrimaryKey && p.Info.GenerateType == IdentityGenerateType.None))
                        .ToList();
                }

                //判断实体类型有是不是编译的代理类型，如果不是，取非null的属性，否则使用IsModified判断
                var isNotCompiled = entityType.IsNotCompiled();
                var modified = isNotCompiled ?
                    properties.Where(p => !PropertyValue.IsEmpty(entity.GetValue(p))).Select(p => p.Name).ToArray() :
                    entity.GetModifiedProperties();

                modified.Where(s => !maxProperties.Contains(s)).ForEach(s => maxProperties.Add(s));

                if (batchOpt == null || batchOpt.CheckModifiedKinds == BatchCheckModifiedKinds.First)
                {
                    break;
                }
            }

             return maxProperties;
        }
    }
}