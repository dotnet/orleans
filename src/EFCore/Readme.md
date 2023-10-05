# Orleans Entity Framework providers - Migrations

Whenever you make a change to the database structures, you need to update the schema. For that to work, we use Entity Framework Migrations.

The generated Migrations files should be kept under source control so we can apply/revert changes to the database structure as we necessary.

Those are examples of how to add a new migration (run from the project directory):

```shell
dotnet ef migrations add InitialSchema -o Data/Migrations
```

In other words:

```shell
dotnet ef migrations add <Migration Name> -o <the output directory>
```

To generate SQL script:

```shell
dotnet ef migrations script -i -o Migrations.sql
```
