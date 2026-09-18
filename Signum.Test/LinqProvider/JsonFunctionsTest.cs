using Xunit.Sdk;
using Signum.Engine.Maps;

namespace Signum.Test.LinqProvider;

public class JsonFunctionsTest
{
    public JsonFunctionsTest()
    {
        MusicStarter.StartAndLoad();
        Connector.CurrentLogger = SqlDumpTextWriter.Enabled ? new SqlDumpTextWriter() : new DebugTextWriter();

        if (Connector.Current is SqlServerConnector con && !con.SupportsJson)
            throw SkipException.ForSkip("Skipping tests because JSON_VALUE requires SQL Server 2016 or later.");
    }

    [Fact]
    public void JsonValueWhere()
    {
        var configs = Database.Query<ConfigEntity>().Where(c => c.Data.JsonValue("$.companyId") == "42").ToList();

        Assert.Single(configs);
        Assert.Empty(Database.Query<ConfigEntity>().Where(c => c.Data.JsonValue("$.companyId") == "43"));
    }

    [Fact]
    public void JsonValueSelect()
    {
        var values = Database.Query<ConfigEntity>().Select(c => new
        {
            CompanyId = c.Data.JsonValue("$.companyId").InSql(),
            SecondTag = c.Data.JsonValue("$.tags[1]").InSql(),
            Max = c.Data.JsonValue("$.limits.max").InSql(),
            Missing = c.Data.JsonValue("$.missing").InSql(),
            Object = c.Data.JsonValue("$.limits").InSql(),
        }).ToList();

        var value = Assert.Single(values);
        Assert.Equal("42", value.CompanyId);
        Assert.Equal("b", value.SecondTag);
        Assert.Equal("10", value.Max);
        Assert.Null(value.Missing);

        if (Schema.Current.Settings.IsPostgres)
            Assert.Equal("{\"max\": 10}", value.Object); //#>> returns the JSON text of objects and arrays
        else
            Assert.Null(value.Object); //JSON_VALUE returns NULL for objects and arrays
    }

    [Fact]
    public void JsonValueParse()
    {
        Assert.Single(Database.Query<ConfigEntity>().Where(c => int.Parse(c.Data!.JsonValue("$.limits.max")!) == 10));
        Assert.Equal(10, Database.Query<ConfigEntity>().Select(c => int.Parse(c.Data!.JsonValue("$.limits.max")!).InSql()).SingleEx());
    }

    [Fact]
    public void JsonValueInMemoryMatchesDatabase()
    {
        var config = Database.Query<ConfigEntity>().SingleEx();

        Assert.Equal(config.Data.JsonValue("$.companyId"), Database.Query<ConfigEntity>().Select(c => c.Data.JsonValue("$.companyId").InSql()).SingleEx());
        Assert.Equal(config.Data.JsonValue("$.tags[0]"), Database.Query<ConfigEntity>().Select(c => c.Data.JsonValue("$.tags[0]").InSql()).SingleEx());
    }

    [Fact]
    public void ComputedColumnIsReadFromDatabase()
    {
        var config = Database.Query<ConfigEntity>().SingleEx();

        Assert.Equal("42", config.CompanyId);
        Assert.Equal("42", Database.Query<ConfigEntity>().Select(c => c.CompanyId).SingleEx());
        Assert.Single(Database.Query<ConfigEntity>().Where(c => c.CompanyId == "42"));
    }

    [Fact]
    public void ComputedColumnDefinition()
    {
        var column = (FieldValue)Schema.Current.Field((ConfigEntity c) => c.CompanyId);

        Assert.NotNull(column.ComputedColumn);
        Assert.True(column.ComputedColumn.Value.Persisted);
        Assert.Equal(IsNullable.Yes, column.Nullable);

        if (Schema.Current.Settings.IsPostgres)
        {
            Assert.Contains("#>>", column.ComputedColumn.Value.Expression);
            Assert.Contains("'{companyId}'::text[]", column.ComputedColumn.Value.Expression);
        }
        else
        {
            Assert.Equal("JSON_VALUE(Data, '$.companyId')", column.ComputedColumn.Value.Expression);
        }
    }

    [Fact]
    public void ComputedColumnIsNotWritten()
    {
        using (var tr = new Transaction())
        {
            var config = Database.Query<ConfigEntity>().SingleEx();

            config.Data = """{"companyId":"7"}""";
            config.Execute(ConfigOperation.Save);

            Assert.Equal("7", Database.Query<ConfigEntity>().Select(c => c.CompanyId).SingleEx());

            var newConfig = new ConfigEntity { Data = """{"companyId":"8"}""" }.Execute(ConfigOperation.Save);

            Assert.Equal("8", Database.Query<ConfigEntity>().Where(c => c.Is(newConfig)).Select(c => c.CompanyId).SingleEx());

            //tr.Commit(); rollback
        }
    }

    [Fact]
    public void ComputedColumnIsSynchronized()
    {
        //The definition stored by the database (SQL Server: (json_value([Data],'$.companyId'))) has to be recognized as equal to the generated one
        var script = Administrator.TotalSynchronizeScript(out _, interactive: false, schemaOnly: true);

        var column = (FieldValue)Schema.Current.Field((ConfigEntity c) => c.CompanyId);
        if (script != null)
            Assert.DoesNotContain(column.Name, script.PlainSql());
    }

    [Fact]
    public void ComputedColumnCanNotBeUpdated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Database.Query<ConfigEntity>().UnsafeUpdate().Set(c => c.CompanyId, c => "1").Execute());

        var column = (FieldValue)Schema.Current.Field((ConfigEntity c) => c.CompanyId);
        Assert.Contains(column.Name, ex.Message);
    }
}
