﻿using Dapper;
using Dapper.CX.SqlServer;
using DataTables.Library;
using Microsoft.Data.SqlClient;
using SqlIntegration.Library.Exceptions;
using SqlIntegration.Library.Extensions;
using SqlIntegration.Library.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace SqlIntegration.Library
{
    public class SqlMigrator<TIdentity>
    {
        private const string Schema = "migrate";        
        private const string KeyMapTableName = "KeyMap";

        private SqlMigrator() 
        {
            // can't create migrator directly, use InitializeAsync instead
        }

        public Func<SqlConnection, string, TIdentity, DataRow, Task> LogMappingException { get; set; }

        public Func<SqlConnection, DataRow, Exception, Task<bool>> OnInsertException { get; set; }

        public Func<Exception, SqlConnection, DbObject, TIdentity, IDbTransaction, Task<TIdentity>> OnMappingException { get; set; }

        private static Dictionary<Type, KeyMapTableInfo> SupportedIdentityTypes
        {
            get 
            {
                return new Dictionary<Type, KeyMapTableInfo>()
                {
                    { typeof(int), new KeyMapTableInfo() { SqlTypeName = "int", MapTableSuffix = "int" } },
                    { typeof(long), new KeyMapTableInfo() { SqlTypeName = "bigint", MapTableSuffix = "long" } },
                    { typeof(Guid), new KeyMapTableInfo() { SqlTypeName = "uniqueidentifier", MapTableSuffix = "guid" } }
                };
            }
        }

        public async static Task<SqlMigrator<TIdentity>> InitializeAsync(SqlConnection cn)
        {
            await CreateDbObjectsAsync(cn);
            return new SqlMigrator<TIdentity>();
        }

        private static async Task CreateDbObjectsAsync(SqlConnection cn)
        {
            if (!(await cn.SchemaExistsAsync(Schema)))
            {
                await cn.ExecuteAsync($"CREATE SCHEMA [{Schema}]");
            }

            string tableName = GetTableName(out KeyMapTableInfo info);            
            if (!(await cn.TableExistsAsync(Schema, tableName)))
            {
                await cn.ExecuteAsync($@"CREATE TABLE [{Schema}].[{tableName}] (
                    [Id] bigint identity(1,1) PRIMARY KEY,
                    [Timestamp] datetime NOT NULL DEFAULT (getutcdate()),
                    [Schema] nvarchar(100) NOT NULL,
                    [TableName] nvarchar(100) NOT NULL,
                    [SourceId] {info.SqlTypeName} NOT NULL,
                    [NewId] {info.SqlTypeName} NOT NULL,
                    CONSTRAINT [U_KeyMap_{info.MapTableSuffix}_SourceId] UNIQUE ([Schema], [TableName], [SourceId]),
                    CONSTRAINT [U_KeyMap_{info.MapTableSuffix}_NewId] UNIQUE ([Schema], [TableName], [NewId])                    
                )");
            }
        }

        public DbObject KeyMapTable { get { return new DbObject(Schema, GetTableName()); } }

        private static string GetTableName() => GetTableName(out _);

        private static string GetTableName(out KeyMapTableInfo info)
        {
            try
            {
                info = SupportedIdentityTypes[typeof(TIdentity)];
                return $"{KeyMapTableName}_{info.MapTableSuffix}";
            }
            catch (Exception exc)
            {
                string supportedTypes = string.Join(", ", SupportedIdentityTypes.Keys.Select(k => k.Name));
                throw new Exception($"Error getting KeyMap table name: {exc.Message}. Make sure you're using a supported type: {supportedTypes}");
            }
        }

        public async Task DeleteMappingsAsync(SqlConnection connection, IEnumerable<DbObject> dbObjects)
        {
            foreach (var obj in dbObjects)
            {
                await connection.ExecuteAsync($"DELETE [{Schema}].[{GetTableName()}] WHERE [Schema]=@schema AND [TableName]=@name", obj);
            }
        }

        public SqlServerCmd MigrateCommand { get; private set; }

        public SqlServerCmd MappingCommand { get; private set; }        

        public async Task<int> CopySelfAsync(
            SqlConnection connection,
            string fromSchema, string fromTable, string identityColumn,
            string criteria = null, object parameters = null,
            Dictionary<string, string> mapForeignKeys = null,
            Action<SqlServerCmd, DataRow> onEachRow = null,
            Action<IDbTransaction> onSuccess = null, 
            Action<Exception, IDbTransaction> onException = null, int maxRows = 0)
        {
            DataTable dataTable = await GetSourceDataAsync(connection, fromSchema, fromTable, criteria, parameters);
            int result = 0;

            if (onSuccess != null && onException != null)
            { 
                using (var txn = connection.BeginTransaction())
                {
                    try
                    {
                        result = await CopyRowsAsync(connection, dataTable, identityColumn, fromSchema, fromTable, mapForeignKeys, onEachRow, txn, maxRows);
                        onSuccess.Invoke(txn);
                    }
                    catch (Exception exc)
                    {
                        onException.Invoke(exc, txn);
                        throw;
                    }                    
                }
            }
            else
            {
                result = await CopyRowsAsync(connection, dataTable, identityColumn, fromSchema, fromTable, mapForeignKeys, onEachRow, maxRows: maxRows);
            }

            return result;
        }

        private static async Task<DataTable> GetSourceDataAsync(SqlConnection connection, string fromSchema, string fromTable, string criteria, object parameters)
        {
            string query = $"SELECT * FROM [{fromSchema}].[{fromTable}]";
            if (!string.IsNullOrEmpty(criteria)) query += " WHERE " + criteria;

            var dataTable = await connection.QueryTableAsync(query, parameters);
            return dataTable;
        }

        public async Task<int> CopyRowsAsync(
            SqlConnection connection,
            DataTable fromDataTable, string identityColumn, string intoSchema, string intoTable,
            Dictionary<string, string> mapForeignKeys = null,
            Action<SqlServerCmd, DataRow> onEachRow = null, IDbTransaction txn = null, int maxRows = 0)
        {
            MappingCommand = await SqlServerCmd.FromTableSchemaAsync(connection, Schema, GetTableName(), txn);
            MappingCommand["Schema"] = intoSchema;
            MappingCommand["TableName"] = intoTable;
            MappingCommand["Timestamp"] = DateTime.UtcNow;

            MigrateCommand = await SqlServerCmd.FromTableSchemaAsync(connection, intoSchema, intoTable, txn);
            
            await ValidateForeignKeyMappingAsync(connection, mapForeignKeys, txn);

            int rowCount = 0;
            foreach (DataRow dataRow in fromDataTable.Rows)
            {
                TIdentity sourceId = dataRow.Field<TIdentity>(identityColumn);
                
                // if this row has already been copied, then skip
                if (await IsRowMappedAsync(connection, new DbObject(intoSchema, intoTable), sourceId, txn)) continue;

                if (maxRows > 0 && rowCount > maxRows) break;

                MigrateCommand.BindDataRow(dataRow);

                var mapping = await MapForeignKeysAsync(connection, dataRow, mapForeignKeys, MigrateCommand, txn);

                if (!mapping.success)
                {
                    if (LogMappingException != null)
                    {
                        await LogMappingException.Invoke(connection, mapping.failedColumn, mapping.sourceId, dataRow);
                    }                    
                    continue;
                }

                onEachRow?.Invoke(MigrateCommand, dataRow);

                TIdentity newId = default;
                try
                {
                    // copy the source row to the destination connection                    
                    newId = await MigrateCommand.InsertAsync<TIdentity>(connection, txn);
                }
                catch (Exception exc)
                {
                    if (OnInsertException != null)
                    {
                        var shouldContinue = await OnInsertException.Invoke(connection, dataRow, exc);
                        if (shouldContinue) continue;
                    }

                    throw new InsertException(dataRow, exc);
                }

                try
                {
                    MappingCommand["SourceId"] = sourceId;
                    MappingCommand["NewId"] = newId;                        
                    await MappingCommand.InsertAsync<TIdentity>(connection, txn);
                }
                catch (Exception exc)
                {
                    throw new MappingException<TIdentity>(sourceId, newId, exc);                        
                }
               
                rowCount++;
            }

            return rowCount;
        }        

        private async Task ValidateForeignKeyMappingAsync(SqlConnection connection, Dictionary<string, string> mapForeignKeys, IDbTransaction txn = null)
        {
            if (mapForeignKeys == null) return;

            var validSources = await connection.QueryAsync<string>(
                $@"SELECT [Schema] + '.' + [TableName] AS [Table]
                FROM [{Schema}].[{GetTableName()}]
                GROUP BY [Schema], [TableName]", transaction: txn);

            var invalid = mapForeignKeys.Values.Except(validSources);
            if (invalid.Any())
            {
                string invalidKeys = string.Join(", ", invalid);
                throw new Exception($"There are one or more unrecognized foreign key mappings: {invalidKeys}");
            }
        }        

        public async Task<TIdentity> GetNewIdAsync(SqlConnection cn, DbObject dbObject, TIdentity sourceId, IDbTransaction txn = null)
        {
            try
            {
                return await cn.QuerySingleAsync<TIdentity>(
                    $@"SELECT [NewId] FROM [{Schema}].[{GetTableName()}] WHERE [Schema]=@schema AND [TableName]=@tableName AND [SourceId]=@sourceId",
                    new { schema = dbObject.Schema, tableName = dbObject.Name, sourceId }, txn);
            }
            catch (Exception exc)
            {
                throw new Exception($"Error getting new Id for {dbObject} from source Id {sourceId}: {exc.Message}");
            }
        }

        private async Task<(bool success, string message, string failedColumn, TIdentity sourceId)> MapForeignKeysAsync(SqlConnection connection, DataRow dataRow, Dictionary<string, string> mapForeignKeys, SqlServerCmd cmd, IDbTransaction txn = null)
        {
            if (mapForeignKeys == null) return (true, null, null, default);

            string columnName = null;
            TIdentity sourceId = default;
            try
            {
                foreach (var kp in mapForeignKeys)
                {
                    columnName = kp.Key;
                    if (!dataRow.IsNull(kp.Key))
                    {
                        sourceId = dataRow.Field<TIdentity>(columnName);

                        TIdentity mappedId = default;

                        var dbObj = DbObject.Parse(kp.Value);
                        try
                        {
                            mappedId = await GetNewIdAsync(connection, dbObj, sourceId, txn);
                        }
                        catch (Exception exc)
                        {
                            if (OnMappingException != null)
                            {
                                mappedId = await OnMappingException.Invoke(exc, connection, dbObj, sourceId, txn);
                                if (mappedId.Equals(default)) throw;
                            }
                            else
                            {
                                throw;
                            }                            
                        }

                        cmd[columnName] = mappedId;
                    }
                }
                return (true, null, null, default);
            }
            catch (Exception exc)
            {
                return (false, exc.Message, columnName, sourceId);
            }
        }

        private async Task<bool> IsRowMappedAsync(SqlConnection connection, DbObject dbObject, TIdentity idValue, IDbTransaction txn = null)
        {
            try
            {
                var newId = await GetNewIdAsync(connection, dbObject, idValue, txn);
                return true;
            }
            catch 
            {
                return false;
            }
        }

        public async Task<int> CopyAcrossAsync(
            SqlConnection fromConnection, string fromSchema, string fromTable, string identityColumn, SqlConnection toConnection, string criteria = null, object parameters = null,
            Dictionary<string, string> mapForeignKeys = null,
            Action<SqlServerCmd, DataRow> onEachRow = null,
            Action<IDbTransaction> onSuccess = null,
            Action<Exception, IDbTransaction> onException = null, int maxRows = 0)
        {
            var dataTable = await GetSourceDataAsync(fromConnection, fromSchema, fromTable, criteria, parameters);

            int result = 0;

            if (onSuccess != null && onException != null)
            {
                using (var txn = toConnection.BeginTransaction())
                {
                    try
                    {
                        result = await CopyRowsAsync(toConnection, dataTable, identityColumn, fromSchema, fromTable, mapForeignKeys, onEachRow, txn, maxRows);
                        onSuccess.Invoke(txn);
                    }
                    catch (Exception exc)
                    {
                        onException.Invoke(exc, txn);
                        throw;
                    }
                }
            }
            else
            {
                result = await CopyRowsAsync(toConnection, dataTable, identityColumn, fromSchema, fromTable, mapForeignKeys, onEachRow, maxRows: maxRows);
            }

            return result;
        }
    }
}
