using Signum.Engine.Maps;

namespace Signum.Engine.Linq;

/// <summary>
/// Translates a lambda over an entity into the alias-free SQL text of a computed column definition.
/// </summary>
internal static class ComputedColumnTranslator
{
    public static string Translate(Table table, LambdaExpression expression)
    {
        var settings = Schema.Current.Settings;

        using (ExpressionMetadataStore.Scope())
        {
            var aliasGenerator = new AliasGenerator();
            var binder = new QueryBinder(aliasGenerator);

            Alias alias = aliasGenerator.Table(table.Name);
            var tableExpression = new TableExpression(alias, table, null, null);
            var projector = table.GetProjectorExpression(alias, binder, disableAssertAllowed: true);

            var lambda = (LambdaExpression)DbQueryProvider.Clean(expression, filter: false, null)!;

            Expression bound = binder.MapVisitExpand(lambda, projector, tableExpression);

            Expression nominated;
            try
            {
                nominated = DbExpressionNominator.FullNominate(bound);
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"The computed column expression '{expression}' of {table.Type.TypeName()} can not be translated to SQL", e);
            }

            var foreignAliases = UsedAliasGatherer.Externals(nominated).Where(a => a != alias).ToList();
            if (foreignAliases.Any())
                throw new InvalidOperationException($"The computed column expression '{expression}' of {table.Type.TypeName()} can only use columns of the same table (no joins or navigation to other entities)");

            NoSubqueryValidator.Validate(nominated, expression, table);

            if (!settings.IsDbType(nominated.Type.UnNullify()))
                throw new InvalidOperationException($"The computed column expression '{expression}' of {table.Type.TypeName()} returns {nominated.Type.TypeName()}, that is not a database type");

            Expression rewritten = settings.IsPostgres ?
                ConditionsRewriterPostgres.Rewrite(nominated) :
                ConditionsRewriter.RewriteAsValue(nominated);

            if (!settings.IsPostgres && nominated.Type.UnNullify() == typeof(bool))
                rewritten = new SqlCastExpression(nominated.Type, rewritten);

            return QueryFormatter.FormatComputedColumn(rewritten);
        }
    }

    class NoSubqueryValidator : DbExpressionVisitor
    {
        LambdaExpression expression;
        Table table;

        NoSubqueryValidator(LambdaExpression expression, Table table)
        {
            this.expression = expression;
            this.table = table;
        }

        public static void Validate(Expression nominated, LambdaExpression expression, Table table)
        {
            new NoSubqueryValidator(expression, table).Visit(nominated);
        }

        Exception NotAllowed(Expression exp) =>
            new InvalidOperationException($"The computed column expression '{expression}' of {table.Type.TypeName()} can not contain sub-queries, aggregates or collections ({exp.GetType().Name})");

        protected internal override Expression VisitSelect(SelectExpression select) => throw NotAllowed(select);
        protected internal override Expression VisitScalar(ScalarExpression scalar) => throw NotAllowed(scalar);
        protected internal override Expression VisitExists(ExistsExpression exists) => throw NotAllowed(exists);
        protected internal override Expression VisitIn(InExpression inExpression) => throw NotAllowed(inExpression);
        protected internal override Expression VisitProjection(ProjectionExpression proj) => throw NotAllowed(proj);
        protected internal override Expression VisitChildProjection(ChildProjectionExpression child) => throw NotAllowed(child);
        protected internal override Expression VisitAggregate(AggregateExpression aggregate) => throw NotAllowed(aggregate);
        protected internal override Expression VisitAggregateRequest(AggregateRequestsExpression request) => throw NotAllowed(request);
        protected internal override Expression VisitMList(MListExpression ml) => throw NotAllowed(ml);
        protected internal override Expression VisitMListProjection(MListProjectionExpression mlp) => throw NotAllowed(mlp);
    }
}
