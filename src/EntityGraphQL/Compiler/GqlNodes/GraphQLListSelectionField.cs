using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Extensions;
using EntityGraphQL.Schema;

namespace EntityGraphQL.Compiler
{
    /// <summary>
    /// Represents a field node in the GraphQL query. That operates on a list of things.
    /// query MyQuery {
    ///     people { # GraphQLListSelectionField
    ///         id, name
    ///     }
    ///     person(id: "") { id }
    /// }
    /// </summary>
    public class GraphQLListSelectionField : BaseGraphQLQueryField
    {
        public Expression ListExpression { get; internal set; }

        /// <summary>
        /// Create a new GraphQLQueryNode. Represents both fields in the query as well as the root level fields on the Query type
        /// </summary>
        /// <param name="schema">The Schema Provider that defines the graphql schema</param>
        /// <param name="field">Field from the schema that this GraphQLListSelectionField is built from</param>
        /// <param name="name">Name of the field. Could be the alias that the user provided</param>
        /// <param name="nextFieldContext">A context for a field building on this. This will be the list element parameter</param>
        /// <param name="rootParameter">Root parameter used by this nodeExpression (movie in example above).</param>
        /// <param name="nodeExpression">Expression for the list</param>
        /// <param name="context">Partent node</param>
        /// <param name="arguments"></param>
        public GraphQLListSelectionField(ISchemaProvider schema, IField? field, string name, ParameterExpression? nextFieldContext, ParameterExpression? rootParameter, Expression nodeExpression, IGraphQLNode context, Dictionary<string, object>? arguments)
            : base(schema, field, name, nextFieldContext, rootParameter, context, arguments)
        {
            this.ListExpression = nodeExpression;
            constantParameters = new Dictionary<ParameterExpression, object>();
        }

        /// <summary>
        /// The dotnet Expression for this node. Could be as simple as (Person p) => p.Name
        /// Or as complex as (DbContext ctx) => ctx.People.Where(...).Select(p => new {...}).First()
        /// If there is a object selection (new {} in a Select() or not) we will build the NodeExpression on
        /// Execute() so we can look up any query fragment selections
        /// </summary>
        public override Expression? GetNodeExpression(IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, ParameterExpression? docParam, object? docVariables, ParameterExpression schemaContext, bool withoutServiceFields, Expression? replacementNextFieldContext, bool isRoot, bool contextChanged, ParameterReplacer replacer)
        {
            var listContext = ListExpression;
            ParameterExpression? nextFieldContext = (ParameterExpression)NextFieldContext!;
            if (contextChanged && Name != "__typename" && replacementNextFieldContext != null)
            {
                var possibleField = replacementNextFieldContext.Type.GetField(Name);
                if (possibleField != null)
                    listContext = Expression.Field(replacementNextFieldContext, possibleField);
                else
                    listContext = isRoot ? replacementNextFieldContext! : replacer.ReplaceByType(listContext, ParentNode!.NextFieldContext!.Type, replacementNextFieldContext!);
                nextFieldContext = Expression.Parameter(listContext.Type.GetEnumerableOrArrayType()!, $"{nextFieldContext.Name}2");
            }
            (listContext, var argumentValues) = Field?.GetExpression(listContext!, null, ParentNode!, schemaContext, ResolveArguments(Arguments), docParam, docVariables, directives, contextChanged, replacer) ?? (ListExpression, null);
            if (argumentValues != null)
                constantParameters[Field!.ArgumentParam!] = argumentValues;
            if (listContext == null)
                return null;

            (listContext, var newNextFieldContext) = ProcessExtensionsPreSelection(listContext, nextFieldContext, replacer);
            if (newNextFieldContext != null)
                nextFieldContext = newNextFieldContext;

            var selectionFields = GetSelectionFields(serviceProvider, fragments, docParam, docVariables, withoutServiceFields, nextFieldContext, schemaContext, contextChanged, replacer);

            if (selectionFields == null || !selectionFields.Any())
            {
                if (withoutServiceFields && Field?.Services.Any() == true)
                    return null;
                return listContext;
            }

            (listContext, selectionFields, nextFieldContext) = ProcessExtensionsSelection(listContext, selectionFields, nextFieldContext, contextChanged, replacer);
            // build a .Select(...) - returning a IEnumerable<>
            var resultExpression = ExpressionUtil.MakeSelectWithDynamicType(nextFieldContext!, listContext, selectionFields.ExpressionOnly());

            if (!withoutServiceFields)
            {
                // if selecting final graph make sure lists are evaluated
                if (!isRoot && resultExpression.Type.IsEnumerableOrArray() && !resultExpression.Type.IsDictionary())
                    resultExpression = ExpressionUtil.MakeCallOnEnumerable("ToList", new[] { resultExpression.Type.GetEnumerableOrArrayType()! }, resultExpression);
            }

            return resultExpression;
        }

        protected override Dictionary<string, CompiledField>? GetSelectionFields(IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, ParameterExpression? docParam, object? docVariables, bool withoutServiceFields, Expression nextFieldContext, ParameterExpression schemaContext, bool contextChanged, ParameterReplacer replacer)
        {
            var fields = base.GetSelectionFields(serviceProvider, fragments, docParam, docVariables, withoutServiceFields, nextFieldContext, schemaContext, contextChanged, replacer);

            // extract possible fields from listContext (might be .Where(), OrderBy() etc)
            if (withoutServiceFields && fields != null)
            {
                var extractor = new ExpressionExtractor();
                var extractedFields = extractor.Extract(ListExpression, (ParameterExpression)nextFieldContext, true);
                if (extractedFields != null)
                    extractedFields.ToDictionary(i => i.Key, i =>
                    {
                        var replaced = replacer.ReplaceByType(i.Value, nextFieldContext.Type, nextFieldContext);
                        return new CompiledField(new GraphQLExtractedField(schema, i.Key, replaced, NextFieldContext!), replaced);
                    })
                    .ToList()
                    .ForEach(i =>
                    {
                        if (!fields.ContainsKey(i.Key))
                            fields.Add(i.Key, i.Value);
                    });
            }

            return fields;
        }
    }
}
