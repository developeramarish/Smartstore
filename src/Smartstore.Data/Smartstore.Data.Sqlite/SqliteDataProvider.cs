﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Smartstore.Data.Providers;

namespace Smartstore.Data.Sqlite
{
    internal class SqliteDataProvider : DataProvider
    {
        public SqliteDataProvider(DatabaseFacade database)
            : base(database)
        {
        }

        public override DbSystemType ProviderType => DbSystemType.SQLite;

        public override DataProviderFeatures Features
            => DataProviderFeatures.Shrink
            | DataProviderFeatures.ReIndex
            | DataProviderFeatures.ComputeSize
            | DataProviderFeatures.AccessIncrement
            | DataProviderFeatures.ReadSequential
            | DataProviderFeatures.ExecuteSqlScript;

        public override DbParameter CreateParameter()
        {
            return new SqliteParameter();
        }

        public override bool MARSEnabled => false;

        public override string EncloseIdentifier(string identifier)
        {
            Guard.NotEmpty(identifier, nameof(identifier));
            return identifier.EnsureStartsWith('"').EnsureEndsWith('"');
        }

        public override string ApplyPaging(string sql, int skip, int take)
        {
            Guard.NotNegative(skip);
            Guard.NotNegative(take);

            return $@"{sql}
LIMIT {take} OFFSET {skip}";
        }

        protected override ValueTask<bool> HasDatabaseCore(string databaseName, bool async)
        {
            return ValueTask.FromResult(true);
        }

        protected override ValueTask<bool> HasTableCore(string tableName, bool async)
        {
            FormattableString sql = $@"SELECT name FROM sqlite_master WHERE type = 'table' AND name = {tableName}";
            return async
                ? Database.ExecuteQueryInterpolatedAsync<string>(sql).AnyAsync()
                : ValueTask.FromResult(Database.ExecuteQueryInterpolated<string>(sql).Any());
        }

        protected override ValueTask<bool> HasColumnCore(string tableName, string columnName, bool async)
        {
            FormattableString sql = $@"SELECT name FROM pragma_table_info({tableName}) WHERE name = {columnName}";
            return async
                ? Database.ExecuteQueryInterpolatedAsync<string>(sql).AnyAsync()
                : ValueTask.FromResult(Database.ExecuteQueryInterpolated<string>(sql).Any());
        }

        protected override ValueTask<string[]> GetTableNamesCore(bool async)
        {
            FormattableString sql = $@"SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            return async
                ? Database.ExecuteQueryInterpolatedAsync<string>(sql).AsyncToArray()
                : ValueTask.FromResult(Database.ExecuteQueryInterpolated<string>(sql).ToArray());
        }

        protected override Task<int> TruncateTableCore(string tableName, bool async)
        {
            var sql = $"DELETE FROM \"{tableName}\"";
            return async
                ? Database.ExecuteSqlRawAsync(sql)
                : Task.FromResult(Database.ExecuteSqlRaw(sql));
        }

        protected override async Task<int> InsertIntoCore(string sql, bool async, params object[] parameters)
        {
            sql += "; SELECT last_insert_rowid();";
            return async
                ? await Database.ExecuteQueryRawAsync<int>(sql, parameters).FirstOrDefaultAsync()
                : Database.ExecuteQueryRaw<int>(sql, parameters).FirstOrDefault();
        }

        public override bool IsTransientException(Exception ex)
            => ex is SqliteException sqliteException
                ? sqliteException.IsTransient
                : ex is TimeoutException;

        public override bool IsUniquenessViolationException(DbUpdateException updateException)
        {
            if (updateException?.InnerException is SqliteException ex)
            {
                // SQLiteErrorCode.Constraint = 10
                return ex.SqliteErrorCode == 19;
            }

            return false;
        }

        protected override Task<long> GetDatabaseSizeCore(bool async)
        {
            // TODO: Get actual file size
            var sql = $"SELECT page_count * page_size as size FROM pragma_page_count(), pragma_page_size()";
            return async
                ? Database.ExecuteQueryRawAsync<long>(sql).FirstOrDefaultAsync().AsTask()
                : Task.FromResult(Database.ExecuteQueryRaw<long>(sql).FirstOrDefault());
        }

        protected override Task<int> ShrinkDatabaseCore(bool async, CancellationToken cancelToken = default)
        {
            // TODO: Lock
            var sql = $"VACUUM;PRAGMA wal_checkpoint=TRUNCATE;PRAGMA optimize;PRAGMA wal_autocheckpoint;";
            return async
                ? Database.ExecuteSqlRawAsync(sql, cancelToken)
                : Task.FromResult(Database.ExecuteSqlRaw(sql));
        }

        protected override Task<int> ReIndexTablesCore(bool async, CancellationToken cancelToken = default)
        {
            // TODO: Lock
            var sql = $"REINDEX;";
            return async
                ? Database.ExecuteSqlRawAsync(sql, cancelToken)
                : Task.FromResult(Database.ExecuteSqlRaw(sql));
        }

        protected override async Task<int?> GetTableIncrementCore(string tableName, bool async)
        {
            var sql = $"SELECT seq FROM sqlite_sequence WHERE name = \"{tableName}\"";

            return async
               ? (await Database.ExecuteScalarRawAsync<int>(sql)).Convert<int?>()
               : Database.ExecuteScalarRaw<int>(sql).Convert<int?>();
        }

        protected override Task SetTableIncrementCore(string tableName, int ident, bool async)
        {
            var sql = $"UPDATE sqlite_sequence SET seq = {ident} WHERE name = \"{tableName}\"";
            return async
               ? Database.ExecuteSqlRawAsync(sql)
               : Task.FromResult(Database.ExecuteSqlRaw(sql));
        }

        protected override IList<string> SplitSqlScript(string sqlScript)
        {
            var commands = new List<string>();
            var lines = sqlScript.GetLines(true);
            var command = string.Empty;
            var delimiter = ";";

            foreach (var line in lines)
            {
                // Ignore comments
                if (line.StartsWith("--"))
                {
                    continue;
                }

                if (!line.EndsWith(delimiter))
                {
                    command += line + Environment.NewLine;
                }
                else
                {
                    command += line[..^delimiter.Length];
                    commands.Add(command);
                    command = string.Empty;
                }
            }

            return commands;
        }

        protected override Stream OpenBlobStreamCore(string tableName, string blobColumnName, string pkColumnName, object pkColumnValue)
        {
            return new SqlBlobStream(this, tableName, blobColumnName, pkColumnName, pkColumnValue);
        }
    }
}