﻿using AdoUtil;
using Dapper;
using Postulate.SqlServer;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace Postulate.Integration.SqlServer
{
    public class Migrator
    {
        public async Task MergeAsync<TIdentity>(
            SqlConnection sourceConnection, string sourceObject, SqlConnection destConnection, 
            string destTable, string identityColumn, IEnumerable<string> keyColumns, 
            bool truncateFirst)
        {
            await MergeAsync<TIdentity>(
                sourceConnection, DbObject.Parse(sourceObject), destConnection, 
                DbObject.Parse(destTable), identityColumn, keyColumns, 
                truncateFirst);
        }

        public async Task MergeAsync<TIdentity>(
            SqlConnection sourceConnection, DbObject sourceObject, 
            SqlConnection destConnection, DbObject destObject, string identityColumn, IEnumerable<string> keyColumns, 
            bool truncateFirst)
        {
            if (truncateFirst)
            {
                await destConnection.ExecuteAsync($"TRUNCATE [{destObject.Schema}].[{destObject.Name}]");
            }

            var cmd = await SqlServerCmd.FromTableSchemaAsync(destConnection, destObject.Schema, destObject.Name, keyColumns);
            cmd.IdentityInsert = true;
            if (string.IsNullOrEmpty(cmd.IdentityColumn) && !string.IsNullOrEmpty(identityColumn)) cmd.IdentityColumn = identityColumn;

            var data = sourceConnection.QueryTable($"SELECT * FROM [{sourceObject.Schema}].[{sourceObject.Name}]");

            foreach (DataRow row in data.Rows)
            {
                cmd.BindDataRow(row);
                await cmd.MergeAsync<TIdentity>(destConnection);
            }
        }        
    }
}
