using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace CloneDeploy_Server.Backend.Data;

// EF exposes replacement of IMigrationsModelDiffer, but its default implementation is internal API.
#pragma warning disable EF1001
public sealed class CloneDeployModelDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : MigrationsModelDiffer(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider,
        rowIdentityMapFactory, commandBatchPreparerDependencies)
{
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target) =>
        SqlChanged(source, target) || base.HasDifferences(source, target);

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = base.GetDifferences(source, target);
        if (!SqlChanged(source, target)) return operations;

        List<MigrationOperation> result = [];
        // Remove dependent objects before changing tables; recreate them after the schema is ready.
        if (Sql(source, PostgreSqlSchema.DropAnnotation) is { } drop)
            result.Add(new SqlOperation { Sql = drop });
        result.AddRange(operations);
        if (Sql(target, PostgreSqlSchema.CreateAnnotation) is { } create)
            result.Add(new SqlOperation { Sql = create });
        return result;
    }

    private static bool SqlChanged(IRelationalModel? source, IRelationalModel? target) =>
        Sql(source, PostgreSqlSchema.CreateAnnotation) != Sql(target, PostgreSqlSchema.CreateAnnotation)
        || Sql(source, PostgreSqlSchema.DropAnnotation) != Sql(target, PostgreSqlSchema.DropAnnotation);

    private static string? Sql(IRelationalModel? model, string annotation) =>
        model?.Model.FindAnnotation(annotation)?.Value as string;
}
#pragma warning restore EF1001
