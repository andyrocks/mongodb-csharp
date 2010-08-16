﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Commands;
using MongoDB.Linq.Expressions;
using MongoDB.Linq.Translators;
using MongoDB.Util;
using System.Collections;

namespace MongoDB.Linq
{
    /// <summary>
    /// 
    /// </summary>
    internal class MongoQueryProvider : IQueryProvider
    {
        private readonly IMongoCollection _collection;

        /// <summary>
        /// Gets the name of the collection.
        /// </summary>
        /// <value>The name of the collection.</value>
        public IMongoCollection Collection
        {
            get { return _collection; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoQueryProvider"/> class.
        /// </summary>
        /// <param name="database">The database.</param>
        /// <param name="collectionName">Name of the collection.</param>
        public MongoQueryProvider(IMongoCollection collection)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            _collection = collection;
        }

        /// <summary>
        /// Creates the query.
        /// </summary>
        /// <typeparam name="TElement">The type of the element.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns></returns>
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new MongoQuery<TElement>(this, expression);
        }

        /// <summary>
        /// Constructs an <see cref="T:System.Linq.IQueryable"/> object that can evaluate the query represented by a specified expression tree.
        /// </summary>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <returns>
        /// An <see cref="T:System.Linq.IQueryable"/> that can evaluate the query represented by the specified expression tree.
        /// </returns>
        public IQueryable CreateQuery(Expression expression)
        {
            Type elementType = TypeHelper.GetElementType(expression.Type);
            try
            {
                return (IQueryable)Activator.CreateInstance(typeof(MongoQuery<>).MakeGenericType(elementType), new object[] { this, expression });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>
        /// Executes the specified expression.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns></returns>
        public TResult Execute<TResult>(Expression expression)
        {
            object result = Execute(expression);
            return (TResult)result;
        }

        /// <summary>
        /// Executes the query represented by a specified expression tree.
        /// </summary>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <returns>
        /// The value that results from executing the specified query.
        /// </returns>
        public object Execute(Expression expression)
        {
            var plan = BuildExecutionPlan(expression);

            var lambda = expression as LambdaExpression;
            if (lambda != null)
            {
                var fn = Expression.Lambda(lambda.Type, plan, lambda.Parameters);
                return fn.Compile();
            }
            else
            {
                var efn = Expression.Lambda<Func<object>>(Expression.Convert(plan, typeof(object)));
                var fn = efn.Compile();
                return fn();
            }
        }

        /// <summary>
        /// Gets the query object.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns></returns>
        internal MongoQueryObject GetQueryObject(Expression expression)
        {
            var projection = Translate(expression);
            return new MongoQueryObjectBuilder().Build(projection);
        }

        /// <summary>
        /// Executes the query object.
        /// </summary>
        /// <param name="queryObject">The query object.</param>
        /// <returns></returns>
        internal object ExecuteQueryObject(MongoQueryObject queryObject){
            if (queryObject.IsCount)
                return ExecuteCount(queryObject);
            if (queryObject.IsMapReduce)
                return ExecuteMapReduce(queryObject);
            return ExecuteFind(queryObject);
        }

        private Expression BuildExecutionPlan(Expression expression)
        {
            var lambda = expression as LambdaExpression;
            if (lambda != null)
                expression = lambda.Body;

            var projection = Translate(expression);

            var rootQueryable = new RootQueryableFinder().Find(expression);
            var provider = Expression.Convert(
                Expression.Property(rootQueryable, typeof(IQueryable).GetProperty("Provider")),
                typeof(MongoQueryProvider));

            return new ExecutionBuilder().Build(projection, provider);
        }

        private Expression Translate(Expression expression)
        {
            var rootQueryable = new RootQueryableFinder().Find(expression);
            var elementType = ((IQueryable)((ConstantExpression)rootQueryable).Value).ElementType;

            expression = PartialEvaluator.Evaluate(expression, CanBeEvaluatedLocally);

            expression = new FieldBinder().Bind(expression, elementType);
            expression = new QueryBinder(this, expression).Bind(expression);
            expression = new AggregateRewriter().Rewrite(expression);
            expression = new RedundantFieldRemover().Remove(expression);
            expression = new RedundantSubqueryRemover().Remove(expression);

            expression = new OrderByRewriter().Rewrite(expression);
            expression = new RedundantFieldRemover().Remove(expression);
            expression = new RedundantSubqueryRemover().Remove(expression);

            return expression;
        }

        /// <summary>
        /// Determines whether this instance [can be evaluated locally] the specified expression.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>
        /// 	<c>true</c> if this instance [can be evaluated locally] the specified expression; otherwise, <c>false</c>.
        /// </returns>
        private bool CanBeEvaluatedLocally(Expression expression)
        {
            // any operation on a query can't be done locally
            ConstantExpression cex = expression as ConstantExpression;
            if (cex != null)
            {
                IQueryable query = cex.Value as IQueryable;
                if (query != null && query.Provider == this)
                    return false;
            }
            MethodCallExpression mc = expression as MethodCallExpression;
            if (mc != null && (mc.Method.DeclaringType == typeof(Enumerable) || mc.Method.DeclaringType == typeof(Queryable) || mc.Method.DeclaringType == typeof(MongoQueryable)))
            {
                return false;
            }
            if (expression.NodeType == ExpressionType.Convert &&
                expression.Type == typeof(object))
                return true;
            return expression.NodeType != ExpressionType.Parameter &&
                   expression.NodeType != ExpressionType.Lambda;
        }

        /// <summary>
        /// Executes the count.
        /// </summary>
        /// <param name="queryObject">The query object.</param>
        /// <returns></returns>
        private object ExecuteCount(MongoQueryObject queryObject)
        {
            if (queryObject.Query == null)
                return (int)_collection.Count();

            return (int)_collection.Count(queryObject.Query);
        }

        private object ExecuteFind(MongoQueryObject queryObject)
        {
            var cursor = queryObject.Collection.FindAll();
            Document spec;
            if (queryObject.Sort != null)
            {
                spec = new Document
                {
                    {"query", queryObject.Query}, 
                    {"orderby", queryObject.Sort}
                };
            }
            else
                spec = queryObject.Query;

            cursor.Spec(spec);
            if (queryObject.Fields.Count > 0)
                cursor.Fields(queryObject.Fields);
            cursor.Limit(queryObject.NumberToLimit);
            cursor.Skip(queryObject.NumberToSkip);

            var executor = GetExecutor(queryObject.DocumentType, queryObject.Projector, queryObject.Aggregator, true);
            return executor.Compile().DynamicInvoke(cursor.Documents);
        }

        private object ExecuteMapReduce(MongoQueryObject queryObject)
        {
            var mapReduce = queryObject.Collection.MapReduce();

            var mapReduceCommand = mapReduce.Command;
            mapReduceCommand.Map = new Code(queryObject.MapFunction);
            mapReduceCommand.Reduce = new Code(queryObject.ReduceFunction);
            mapReduceCommand.Finalize = new Code(queryObject.FinalizerFunction);
            mapReduceCommand.Query = queryObject.Query;

            if(queryObject.Sort != null)
                mapReduceCommand.Sort = queryObject.Sort;

            mapReduceCommand.Limit = queryObject.NumberToLimit;

            if (queryObject.NumberToSkip != 0)
                throw new InvalidQueryException("MapReduce queries do no support Skips.");

            var executor = GetExecutor(typeof(Document), queryObject.Projector, queryObject.Aggregator, true);
            return executor.Compile().DynamicInvoke(mapReduce.Documents);
        }

        private static LambdaExpression GetExecutor(Type documentType, LambdaExpression projector,  
            Expression aggregator, bool boxReturn)
        {
            var documents = Expression.Parameter(typeof(IEnumerable), "documents");
            Expression body = Expression.Call(
                typeof(MongoQueryProvider),
                "Project",
                new[] { documentType, projector.Body.Type },
                documents,
                projector);
            if (aggregator != null)
                body = Expression.Invoke(aggregator, body);

            if (boxReturn && body.Type != typeof(object))
                body = Expression.Convert(body, typeof(object));

            return Expression.Lambda(body, documents);
        }

        private static IEnumerable<TResult> Project<TDocument, TResult>(IEnumerable documents, Func<TDocument, TResult> projector)
        {
            return documents.OfType<TDocument>().Select(projector);
        }

        private class RootQueryableFinder : MongoExpressionVisitor
        {
            private Expression _root;

            public Expression Find(Expression expression)
            {
                Visit(expression);
                return _root;
            }

            protected override Expression Visit(Expression exp)
            {
                Expression result = base.Visit(exp);

                if (this._root == null && result != null && typeof(IQueryable).IsAssignableFrom(result.Type))
                {
                    this._root = result;
                }

                return result;
            }
        }
    }
}