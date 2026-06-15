# Known limitations

For now this library does not support the following features:

* **Navigation properties**: The library does not support inserting entities with navigation properties. You can only insert simple entities without any relationships.
* **Change tracking**: The library does not track changes to the entities being inserted. This means that you cannot use the `DbContext.ChangeTracker` to track changes to the entities after they have been inserted.
* **Inheritance**: The library does not support inserting entities with inheritance (TPT, TPH, TPC). You can only insert entities of a single type.

## Oracle

* **Transaction rollback with `ExecuteBulkInsertReturnEntities` / conflict resolution**: These operations rely on a temporary table created and dropped via DDL (`CREATE` / `ALTER` / `DROP TABLE`). In Oracle, DDL statements issue an implicit `COMMIT`, so the inserted/updated rows cannot be rolled back as part of an enclosing transaction. Plain `ExecuteBulkInsert` (without conflict resolution) does not use a temporary table and rolls back correctly.

Please vote for the features you would like to see in the [GitHub issues](https://github.com/PhenX/PhenX.EntityFrameworkCore.BulkInsert/issues).
