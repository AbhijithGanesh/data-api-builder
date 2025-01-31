// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Threading.Tasks;
using Azure.Core;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Tests.SqlTests;
using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    [TestClass, TestCategory(TestCategory.MSSQL)]
    public class SqlQueryExecutorUnitTests
    {
        // Error code for semaphore timeout in MsSql.
        private const int ERRORCODE_SEMAPHORE_TIMEOUT = 121;
        /// <summary>
        /// Validates managed identity token issued ONLY when connection string does not specify
        /// User, Password, and Authentication method.
        /// </summary>
        [DataTestMethod]
        [DataRow("Server =<>;Database=<>;User=xyz;", false, false,
            DisplayName = "No managed identity access token when connection string specifies User only.")]
        [DataRow("Server =<>;Database=<>;Password=xyz;", false, false,
            DisplayName = "No managed identity access token when connection string specifies Password only.")]
        [DataRow("Server =<>;Database=<>;Authentication=Active Directory Integrated;", false, false,
            DisplayName = "No managed identity access token when connection string specifies Authentication method only.")]
        [DataRow("Server =<>;Database=<>;User=xyz;Password=xxx", false, false,
            DisplayName = "No managed identity access token when connection string specifies both User and Password.")]
        [DataRow("Server =<>;Database=<>;UID=xyz;Pwd=xxx", false, false,
            DisplayName = "No managed identity access token when connection string specifies Uid and Pwd.")]
        [DataRow("Server =<>;Database=<>;User=xyz;Authentication=Active Directory Service Principal", false, false,
            DisplayName = "No managed identity access when connection string specifies both User and Authentication method.")]
        [DataRow("Server =<>;Database=<>;Password=xxx;Authentication=Active Directory Password;", false, false,
            DisplayName = "No managed identity access token when connection string specifies both Password and Authentication method.")]
        [DataRow("Server =<>;Database=<>;User=xyz;Password=xxx;Authentication=SqlPassword", false, false,
            DisplayName = "No managed identity access token when connection string specifies User, Password and Authentication method.")]
        [DataRow("Server =<>;Database=<>;Trusted_Connection=yes", false, false,
            DisplayName = "No managed identity access token when connection string specifies Trusted Connection.")]
        [DataRow("Server =<>;Database=<>;Integrated Security=true", false, false,
            DisplayName = "No managed identity access token when connection string specifies Integrated Security.")]
        [DataRow("Server =<>;Database=<>;", true, false,
            DisplayName = "Managed identity access token from config used " +
                "when connection string specifies none of User, Password and Authentication method.")]
        [DataRow("Server =<>;Database=<>;", true, true,
            DisplayName = "Default managed identity access token used " +
                "when connection string specifies none of User, Password and Authentication method")]
        public async Task TestHandleManagedIdentityAccess(
            string connectionString,
            bool expectManagedIdentityAccessToken,
            bool isDefaultAzureCredential)
        {
            RuntimeConfig mockConfig = new(
               Schema: "",
               DataSource: new(DatabaseType.MSSQL, connectionString, new()),
               Runtime: new(
                   Rest: new(),
                   GraphQL: new(),
                   Host: new(null, null)
               ),
               Entities: new(new Dictionary<string, Entity>())
           );

            MockFileSystem fileSystem = new();
            fileSystem.AddFile(FileSystemRuntimeConfigLoader.DEFAULT_CONFIG_FILE_NAME, new MockFileData(mockConfig.ToJson()));
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader);
            Mock<DbExceptionParser> dbExceptionParser = new(provider);
            Mock<ILogger<MsSqlQueryExecutor>> queryExecutorLogger = new();
            Mock<IHttpContextAccessor> httpContextAccessor = new();
            MsSqlQueryExecutor msSqlQueryExecutor = new(provider, dbExceptionParser.Object, queryExecutorLogger.Object, httpContextAccessor.Object);

            const string DEFAULT_TOKEN = "Default access token";
            const string CONFIG_TOKEN = "Configuration controller access token";
            AccessToken testValidToken = new(accessToken: DEFAULT_TOKEN, expiresOn: DateTimeOffset.MaxValue);
            if (expectManagedIdentityAccessToken)
            {
                if (isDefaultAzureCredential)
                {
                    Mock<DefaultAzureCredential> dacMock = new();
                    dacMock
                        .Setup(m => m.GetTokenAsync(It.IsAny<TokenRequestContext>(),
                            It.IsAny<System.Threading.CancellationToken>()))
                        .Returns(ValueTask.FromResult(testValidToken));
                    msSqlQueryExecutor.AzureCredential = dacMock.Object;
                }
                else
                {
                    await provider.Initialize(
                        provider.GetConfig().ToJson(),
                        graphQLSchema: null,
                        connectionString: connectionString,
                        accessToken: CONFIG_TOKEN);
                    msSqlQueryExecutor = new(provider, dbExceptionParser.Object, queryExecutorLogger.Object, httpContextAccessor.Object);
                }
            }

            using SqlConnection conn = new(connectionString);
            await msSqlQueryExecutor.SetManagedIdentityAccessTokenIfAnyAsync(conn);

            if (expectManagedIdentityAccessToken)
            {
                if (isDefaultAzureCredential)
                {
                    Assert.AreEqual(expected: DEFAULT_TOKEN, actual: conn.AccessToken);
                }
                else
                {
                    Assert.AreEqual(expected: CONFIG_TOKEN, actual: conn.AccessToken);
                }
            }
            else
            {
                Assert.AreEqual(expected: default, actual: conn.AccessToken);
            }
        }

        /// <summary>
        /// Test to validate that when a query successfully executes within the allowed number of retries, a result is returned
        /// and no further retries occur.
        /// </summary>
        [TestMethod, TestCategory(TestCategory.MSSQL)]
        public async Task TestRetryPolicyExhaustingMaxAttempts()
        {
            int maxRetries = 5;
            int maxAttempts = maxRetries + 1; // 1 represents the original attempt to execute the query in addition to retries.
            RuntimeConfig mockConfig = new(
               Schema: "",
               DataSource: new(DatabaseType.MSSQL, "", new()),
               Runtime: new(
                   Rest: new(),
                   GraphQL: new(),
                   Host: new(null, null)
               ),
               Entities: new(new Dictionary<string, Entity>())
           );

            MockFileSystem fileSystem = new();
            fileSystem.AddFile(FileSystemRuntimeConfigLoader.DEFAULT_CONFIG_FILE_NAME, new MockFileData(mockConfig.ToJson()));
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader)
            {
                IsLateConfigured = true
            };

            Mock<ILogger<QueryExecutor<SqlConnection>>> queryExecutorLogger = new();
            Mock<IHttpContextAccessor> httpContextAccessor = new();
            DbExceptionParser dbExceptionParser = new MsSqlDbExceptionParser(provider);
            Mock<MsSqlQueryExecutor> queryExecutor
                = new(provider, dbExceptionParser, queryExecutorLogger.Object, httpContextAccessor.Object);

            queryExecutor.Setup(x => x.ConnectionStringBuilders).Returns(new Dictionary<string, DbConnectionStringBuilder>());

            // Mock the ExecuteQueryAgainstDbAsync to throw a transient exception.
            queryExecutor.Setup(x => x.ExecuteQueryAgainstDbAsync(
                It.IsAny<SqlConnection>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, DbConnectionParam>>(),
                It.IsAny<Func<DbDataReader, List<string>, Task<object>>>(),
                It.IsAny<HttpContext>(),
                It.IsAny<List<string>>()))
            .Throws(SqlTestHelper.CreateSqlException(ERRORCODE_SEMAPHORE_TIMEOUT));

            // Call the actual ExecuteQueryAsync method.
            queryExecutor.Setup(x => x.ExecuteQueryAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, DbConnectionParam>>(),
                It.IsAny<Func<DbDataReader, List<string>, Task<object>>>(),
                It.IsAny<HttpContext>(),
                It.IsAny<List<string>>(),
                It.IsAny<string>())).CallBase();

            DataApiBuilderException ex = await Assert.ThrowsExceptionAsync<DataApiBuilderException>(async () =>
            {
                await queryExecutor.Object.ExecuteQueryAsync<object>(
                    sqltext: string.Empty,
                    parameters: new Dictionary<string, DbConnectionParam>(),
                    dataReaderHandler: null,
                    httpContext: null,
                    args: null);
            });

            Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);

            // For each attempt logger is invoked once. Currently we have hardcoded the number of attempts.
            // Once we have number of retry attempts specified in config, we will make it dynamic.
            Assert.AreEqual(maxAttempts, queryExecutorLogger.Invocations.Count);
        }

        /// <summary>
        /// Validates that a query successfully executes within two retries by checking that the SqlQueryExecutor logger
        /// was invoked the expected number of times.
        /// </summary>
        [TestMethod, TestCategory(TestCategory.MSSQL)]
        public async Task TestRetryPolicySuccessfullyExecutingQueryAfterNAttempts()
        {
            TestHelper.SetupDatabaseEnvironment(TestCategory.MSSQL);
            FileSystem fileSystem = new();
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            RuntimeConfigProvider provider = new(loader) { IsLateConfigured = true };
            Mock<ILogger<QueryExecutor<SqlConnection>>> queryExecutorLogger = new();
            Mock<IHttpContextAccessor> httpContextAccessor = new();
            DbExceptionParser dbExceptionParser = new MsSqlDbExceptionParser(provider);
            Mock<MsSqlQueryExecutor> queryExecutor
                = new(provider, dbExceptionParser, queryExecutorLogger.Object, httpContextAccessor.Object);

            queryExecutor.Setup(x => x.ConnectionStringBuilders).Returns(new Dictionary<string, DbConnectionStringBuilder>());

            // Mock the ExecuteQueryAgainstDbAsync to throw a transient exception.
            queryExecutor.SetupSequence(x => x.ExecuteQueryAgainstDbAsync(
                It.IsAny<SqlConnection>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, DbConnectionParam>>(),
                It.IsAny<Func<DbDataReader, List<string>, Task<object>>>(),
                It.IsAny<HttpContext>(),
                It.IsAny<List<string>>()))
            .Throws(SqlTestHelper.CreateSqlException(ERRORCODE_SEMAPHORE_TIMEOUT))
            .Throws(SqlTestHelper.CreateSqlException(ERRORCODE_SEMAPHORE_TIMEOUT))
            .CallBase();

            // Call the actual ExecuteQueryAsync method.
            queryExecutor.Setup(x => x.ExecuteQueryAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, DbConnectionParam>>(),
                It.IsAny<Func<DbDataReader, List<string>, Task<object>>>(),
                It.IsAny<HttpContext>(),
                It.IsAny<List<string>>(),
                It.IsAny<string>())).CallBase();

            string sqltext = "SELECT * from books";

            await queryExecutor.Object.ExecuteQueryAsync<object>(
                    sqltext: sqltext,
                    parameters: new Dictionary<string, DbConnectionParam>(),
                    dataReaderHandler: null,
                    args: null);

            // The logger is invoked three (3) times, once for each of the following events:
            // The query fails on the first attempt (log event 1).
            // The query fails on the second attempt/first retry (log event 2).
            // The query succeeds on the third attempt/second retry (log event 3).
            Assert.AreEqual(3, queryExecutorLogger.Invocations.Count);
        }

        [TestCleanup]
        public void CleanupAfterEachTest()
        {
            TestHelper.UnsetAllDABEnvironmentVariables();
        }
    }
}
