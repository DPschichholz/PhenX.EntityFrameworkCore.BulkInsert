using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

[Trait("Category", "PostgreSql")]
[Collection(TestDbContainerPostgreSqlCollection.Name)]
public class ExternalKeyMergeTestsPostgreSql(TestDbContainerPostgreSql dbContainer) : ExternalKeyMergeTestsBase<TestDbContextPostgreSql>(dbContainer)
{
}

