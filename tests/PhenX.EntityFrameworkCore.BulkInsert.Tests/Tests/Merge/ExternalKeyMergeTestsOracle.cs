using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

[Trait("Category", "Oracle")]
[Collection(TestDbContainerOracleCollection.Name)]
public class ExternalKeyMergeTestsOracle(TestDbContainerOracle dbContainer) : ExternalKeyMergeTestsBase<TestDbContextOracle>(dbContainer)
{
}

