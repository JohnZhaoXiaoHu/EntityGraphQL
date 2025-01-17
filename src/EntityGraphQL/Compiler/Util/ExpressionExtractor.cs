using System.Collections.Generic;
using System.Linq.Expressions;

namespace EntityGraphQL.Compiler.Util
{
    /// <summary>
    /// Extracts expression with the root context as the provided ParameterExpression.
    /// Useful for getting required fields out of a WithService() call
    /// </summary>
    internal class ExpressionExtractor : ExpressionVisitor
    {
        private Expression? rootContext;
        private Dictionary<string, Expression>? extractedExpressions;
        private Expression? currentExpression;
        private string? contextParamFieldName;
        private bool matchByType;

        internal IDictionary<string, Expression>? Extract(Expression node, Expression rootContext, bool matchByType = false)
        {
            this.rootContext = rootContext;
            extractedExpressions = new Dictionary<string, Expression>();
            currentExpression = null;
            contextParamFieldName = null;
            this.matchByType = matchByType;
            Visit(node);
            return extractedExpressions.Count > 0 ? extractedExpressions : null;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (rootContext == null)
                throw new EntityGraphQLCompilerException("Root context not set for ExpressionExtractor");

            if (rootContext == currentExpression)
                throw new EntityGraphQLCompilerException($"The context parameter {node.Name} used in a WithService() field is not allowed. Please select the specific fields required from the context parameter.");
            if ((rootContext == node || (matchByType && rootContext.Type == node.Type)) && currentExpression != null && contextParamFieldName != null)
                extractedExpressions![contextParamFieldName] = currentExpression;
            return base.VisitParameter(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (currentExpression == null)
            {
                currentExpression = node;
                contextParamFieldName = node.Member.Name;
            }
            var result = base.VisitMember(node);
            return result;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Object != null)
            {
                var prevExp = currentExpression;
                currentExpression = null;
                Visit(node.Object);
                currentExpression = prevExp;
            }
            foreach (var arg in node.Arguments)
            {
                currentExpression = null;
                Visit(arg);
            }
            currentExpression = null;
            return node;
        }
    }
}