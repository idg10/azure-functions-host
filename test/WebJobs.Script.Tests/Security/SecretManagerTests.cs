﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.Properties;
using Microsoft.Azure.WebJobs.Script.WebHost.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebJobs.Script.Tests;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Security
{
    public class SecretManagerTests
    {
        private const string TestEncryptionKey = "/a/vXvWJ3Hzgx4PFxlDUJJhQm5QVyGiu0NNLFm/ZMMg=";
        private readonly HostNameProvider _hostNameProvider;
        private readonly TestEnvironment _testEnvironment;
        private readonly StartupContextProvider _startupContextProvider;
        private readonly TestLoggerProvider _testLoggerProvider;

        public SecretManagerTests()
        {
            _testEnvironment = new TestEnvironment();
            _testEnvironment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName, "test.azurewebsites.net");

            _testLoggerProvider = new TestLoggerProvider();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(_testLoggerProvider);
            _hostNameProvider = new HostNameProvider(_testEnvironment);
            _startupContextProvider = new StartupContextProvider(_testEnvironment, loggerFactory.CreateLogger<StartupContextProvider>());
        }

        [Fact]
        public async Task CachedSecrets_UsedWhenPresent()
        {
            using (var directory = new TempDirectory())
            {
                string startupContextPath = Path.Combine(directory.Path, Guid.NewGuid().ToString());
                _testEnvironment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteStartupContextCache, startupContextPath);
                Environment.SetEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey, TestEncryptionKey);

                WriteStartContextCache(startupContextPath);

                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    var functionSecrets = await secretManager.GetFunctionSecretsAsync("function1", true);

                    Assert.Equal(4, functionSecrets.Count);
                    Assert.Equal("function1value", functionSecrets["test-function-1"]);
                    Assert.Equal("function2value", functionSecrets["test-function-2"]);
                    Assert.Equal("hostfunction1value", functionSecrets["test-host-function-1"]);
                    Assert.Equal("hostfunction2value", functionSecrets["test-host-function-2"]);

                    var hostSecrets = await secretManager.GetHostSecretsAsync();

                    Assert.Equal("test-master-key", hostSecrets.MasterKey);
                    Assert.Equal(2, hostSecrets.FunctionKeys.Count);
                    Assert.Equal("hostfunction1value", hostSecrets.FunctionKeys["test-host-function-1"]);
                    Assert.Equal("hostfunction2value", hostSecrets.FunctionKeys["test-host-function-2"]);
                    Assert.Equal(2, hostSecrets.SystemKeys.Count);
                    Assert.Equal("system1value", hostSecrets.SystemKeys["test-system-1"]);
                    Assert.Equal("system2value", hostSecrets.SystemKeys["test-system-2"]);
                }

                var logs = _testLoggerProvider.GetAllLogMessages();
                Assert.Equal($"Loading startup context from {startupContextPath}", logs[0].FormattedMessage);
                Assert.Equal($"Loaded keys for 2 functions from startup context", logs[1].FormattedMessage);
                Assert.Equal($"Loaded host keys from startup context", logs[2].FormattedMessage);
            }
        }

        private void WriteStartContextCache(string path)
        {
            var context = new JObject();
            var hostSecrets = new JObject
            {
                { "master", "test-master-key" },
                {
                    "function", new JObject
                    {
                        { "test-host-function-1", "hostfunction1value" },
                        { "test-host-function-2", "hostfunction2value" }
                    }
                },
                {
                    "system", new JObject
                    {
                        { "test-system-1", "system1value" },
                        { "test-system-2", "system2value" }
                    }
                }
            };
            var functionSecrets = new JArray
            {
                new JObject
                {
                    { "name", "function1" },
                    {
                        "secrets", new JObject
                        {
                            { "test-function-1", "function1value" },
                            { "test-function-2", "function2value" },
                        }
                    }
                },
                new JObject
                {
                    { "name", "function2" },
                    {
                        "secrets", new JObject
                        {
                            { "test-function-1", "function1value" },
                            { "test-function-2", "function2value" },
                        }
                    }
                }
            };
            context.Add("secrets", new JObject
            {
                { "host", hostSecrets },
                { "function", functionSecrets }
            });
            string json = JsonConvert.SerializeObject(context);
            var encryptionKey = Convert.FromBase64String(TestEncryptionKey);
            string encryptedJson = SimpleWebTokenHelper.Encrypt(json, encryptionKey);

            File.WriteAllText(path, encryptedJson);
        }

        [Fact]
        public async Task MergedSecrets_PrioritizesFunctionSecrets()
        {
            using (var directory = new TempDirectory())
            {
                string hostSecrets =
                    @"{
    'masterKey': {
        'name': 'master',
        'value': '1234',
        'encrypted': false
    },
    'functionKeys': [
        {
            'name': 'Key1',
            'value': 'HostValue1',
            'encrypted': false
        },
        {
            'name': 'Key3',
            'value': 'HostValue3',
            'encrypted': false
        }
    ]
}";
                string functionSecrets =
                    @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'FunctionValue1',
            'encrypted': false
        },
        {
            'name': 'Key2',
            'value': 'FunctionValue2',
            'encrypted': false
        }
    ]
}";
                File.WriteAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName), hostSecrets);
                File.WriteAllText(Path.Combine(directory.Path, "testfunction.json"), functionSecrets);

                IDictionary<string, string> result;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    result = await secretManager.GetFunctionSecretsAsync("testfunction", true);
                }

                Assert.Contains("Key1", result.Keys);
                Assert.Contains("Key2", result.Keys);
                Assert.Contains("Key3", result.Keys);
                Assert.Equal("FunctionValue1", result["Key1"]);
                Assert.Equal("FunctionValue2", result["Key2"]);
                Assert.Equal("HostValue3", result["Key3"]);
            }
        }

        [Fact]
        public async Task GetFunctionSecrets_UpdatesStaleSecrets()
        {
            using (var directory = new TempDirectory())
            {
                string functionName = "testfunction";
                string expectedTraceMessage = string.Format(Resources.TraceStaleFunctionSecretRefresh, functionName);
                string functionSecretsJson =
                 @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'FunctionValue1',
            'encrypted': false
        },
        {
            'name': 'Key2',
            'value': 'FunctionValue2',
            'encrypted': false
        }
    ]
}";
                File.WriteAllText(Path.Combine(directory.Path, functionName + ".json"), functionSecretsJson);

                IDictionary<string, string> functionSecrets;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                }

                // Read the persisted content
                var result = JsonConvert.DeserializeObject<FunctionSecrets>(File.ReadAllText(Path.Combine(directory.Path, functionName + ".json")));
                bool functionSecretsConverted = functionSecrets.Values.Zip(result.Keys, (r1, r2) => string.Equals("!" + r1, r2.Value)).All(r => r);

                Assert.Equal(2, result.Keys.Count);
                Assert.True(functionSecretsConverted, "Function secrets were not persisted");
            }
        }

        [Fact]
        public async Task GetHostSecrets_UpdatesStaleSecrets()
        {
            using (var directory = new TempDirectory())
            {
                string expectedTraceMessage = Resources.TraceStaleHostSecretRefresh;
                string hostSecretsJson =
                    @"{
    'masterKey': {
        'name': 'master',
        'value': '1234',
        'encrypted': false
    },
    'functionKeys': [
        {
            'name': 'Key1',
            'value': 'HostValue1',
            'encrypted': false
        },
        {
            'name': 'Key3',
            'value': 'HostValue3',
            'encrypted': false
        }
    ],
    'systemKeys': [
        {
            'name': 'SystemKey1',
            'value': 'SystemHostValue1',
            'encrypted': false
        },
        {
            'name': 'SystemKey2',
            'value': 'SystemHostValue2',
            'encrypted': false
        }
    ]
}";
                File.WriteAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName), hostSecretsJson);

                HostSecretsInfo hostSecrets;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    hostSecrets = await secretManager.GetHostSecretsAsync();
                }

                // Read the persisted content
                var result = JsonConvert.DeserializeObject<HostSecrets>(File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName)));
                bool functionSecretsConverted = hostSecrets.FunctionKeys.Values.Zip(result.FunctionKeys, (r1, r2) => string.Equals("!" + r1, r2.Value)).All(r => r);
                bool systemSecretsConverted = hostSecrets.SystemKeys.Values.Zip(result.SystemKeys, (r1, r2) => string.Equals("!" + r1, r2.Value)).All(r => r);

                Assert.Equal(2, result.FunctionKeys.Count);
                Assert.Equal(2, result.SystemKeys.Count);
                Assert.Equal("!" + hostSecrets.MasterKey, result.MasterKey.Value);
                Assert.True(functionSecretsConverted, "Function secrets were not persisted");
                Assert.True(systemSecretsConverted, "System secrets were not persisted");
            }
        }

        [Fact]
        public async Task GetHostSecrets_WhenNoHostSecretFileExists_GeneratesSecretsAndPersistsFiles()
        {
            using (var directory = new TempDirectory())
            {
                string expectedTraceMessage = Resources.TraceHostSecretGeneration;
                HostSecretsInfo hostSecrets;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false, setStaleValue: false))
                {
                    hostSecrets = await secretManager.GetHostSecretsAsync();
                }

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName));
                HostSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<HostSecrets>(secretsJson);

                Assert.NotNull(hostSecrets);
                Assert.NotNull(persistedSecrets);
                Assert.Equal(1, hostSecrets.FunctionKeys.Count);
                Assert.NotNull(hostSecrets.MasterKey);
                Assert.NotNull(hostSecrets.SystemKeys);
                Assert.Equal(0, hostSecrets.SystemKeys.Count);
                Assert.Equal(persistedSecrets.MasterKey.Value, hostSecrets.MasterKey);
                Assert.Equal(persistedSecrets.FunctionKeys.First().Value, hostSecrets.FunctionKeys.First().Value);
            }
        }

        [Fact]
        public async Task GetFunctionSecrets_WhenNoSecretFileExists_CreatesDefaultSecretAndPersistsFile()
        {
            using (var directory = new TempDirectory())
            {
                string functionName = "TestFunction";
                string expectedTraceMessage = string.Format(Resources.TraceFunctionSecretGeneration, functionName);

                IDictionary<string, string> functionSecrets;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false, setStaleValue: false))
                {
                    functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                }

                bool functionSecretsExists = File.Exists(Path.Combine(directory.Path, "testfunction.json"));

                Assert.NotNull(functionSecrets);
                Assert.True(functionSecretsExists);
                Assert.Equal(1, functionSecrets.Count);
                Assert.Equal(ScriptConstants.DefaultFunctionKeyName, functionSecrets.Keys.First());
            }
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithFunctionNameAndNoSecret_GeneratesFunctionSecretsAndPersistsFile()
        {
            using (var directory = new TempDirectory())
            {
                string secretName = "TestSecret";
                string functionName = "TestFunction";
                string expectedTraceMessage = string.Format(Resources.TraceAddOrUpdateFunctionSecret, "Function", secretName, functionName, "Created");

                KeyOperationResult result;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false))
                {
                    result = await secretManager.AddOrUpdateFunctionSecretAsync(secretName, null, functionName, ScriptSecretsType.Function);
                }

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, "testfunction.json"));
                FunctionSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<FunctionSecrets>(secretsJson);

                Assert.Equal(OperationResult.Created, result.Result);
                Assert.NotNull(result.Secret);
                Assert.NotNull(persistedSecrets);
                Assert.Equal(result.Secret, persistedSecrets.Keys.First().Value);
                Assert.Equal(secretName, persistedSecrets.Keys.First().Name, StringComparer.Ordinal);
            }
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithFunctionNameAndNoSecret_EncryptsSecretAndPersistsFile()
        {
            using (var directory = new TempDirectory())
            {
                string secretName = "TestSecret";
                string functionName = "TestFunction";
                string expectedTraceMessage = string.Format(Resources.TraceAddOrUpdateFunctionSecret, "Function", secretName, functionName, "Created");

                KeyOperationResult result;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    result = await secretManager.AddOrUpdateFunctionSecretAsync(secretName, null, functionName, ScriptSecretsType.Function);
                }

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, "testfunction.json"));
                FunctionSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<FunctionSecrets>(secretsJson);

                Assert.Equal(OperationResult.Created, result.Result);
                Assert.NotNull(result.Secret);
                Assert.NotNull(persistedSecrets);
                Assert.Equal("!" + result.Secret, persistedSecrets.Keys.First().Value);
                Assert.Equal(secretName, persistedSecrets.Keys.First().Name, StringComparer.Ordinal);
                Assert.True(persistedSecrets.Keys.First().IsEncrypted);
            }
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithFunctionNameAndProvidedSecret_UsesSecretAndPersistsFile()
        {
            using (var directory = new TempDirectory())
            {
                string secretName = "TestSecret";
                string functionName = "TestFunction";
                string expectedTraceMessage = string.Format(Resources.TraceAddOrUpdateFunctionSecret, "Function", secretName, functionName, "Created");

                KeyOperationResult result;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false))
                {
                    result = await secretManager.AddOrUpdateFunctionSecretAsync(secretName, "TestSecretValue", functionName, ScriptSecretsType.Function);
                }

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, "testfunction.json"));
                FunctionSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<FunctionSecrets>(secretsJson);

                Assert.Equal(OperationResult.Created, result.Result);
                Assert.Equal("TestSecretValue", result.Secret, StringComparer.Ordinal);
                Assert.NotNull(persistedSecrets);
                Assert.Equal(result.Secret, persistedSecrets.Keys.First().Value);
                Assert.Equal(secretName, persistedSecrets.Keys.First().Name, StringComparer.Ordinal);
            }
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithNoFunctionNameAndProvidedSecret_UsesSecretAndPersistsHostFile()
        {
            await AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(HostKeyScopes.FunctionKeys, h => h.FunctionKeys);
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithSystemSecretScopeAndProvidedSecret_UsesSecretAndPersistsHostFile()
        {
            await AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(HostKeyScopes.SystemKeys, h => h.SystemKeys);
        }

        [Fact]
        public async Task AddOrUpdateFunctionSecrets_WithExistingHostFileAndSystemSecretScope_PersistsHostFileWithSecret()
        {
            using (var directory = new TempDirectory())
            {
                var hostSecret = new HostSecrets();
                hostSecret.MasterKey = new Key("_master", "master");
                hostSecret.FunctionKeys = new List<Key> { };

                var hostJson = JsonConvert.SerializeObject(hostSecret);
                await FileUtility.WriteAsync(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName), hostJson);
                await AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(HostKeyScopes.SystemKeys, h => h.SystemKeys, directory);
            }
        }

        public async Task AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(string scope, Func<HostSecrets, IList<Key>> keySelector)
        {
            using (var directory = new TempDirectory())
            {
                await AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(scope, keySelector, directory);
            }
        }

        public async Task AddOrUpdateFunctionSecrets_WithScope_UsesSecretandPersistsHostFile(string scope, Func<HostSecrets, IList<Key>> keySelector, TempDirectory directory)
        {
            string secretName = "TestSecret";
            string expectedTraceMessage = string.Format(Resources.TraceAddOrUpdateFunctionSecret, "Host", secretName, scope, "Created");

            KeyOperationResult result;
            using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false))
            {
                result = await secretManager.AddOrUpdateFunctionSecretAsync(secretName, "TestSecretValue", scope, ScriptSecretsType.Host);
            }

            string secretsJson = File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName));
            HostSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<HostSecrets>(secretsJson);
            Key newSecret = keySelector(persistedSecrets).FirstOrDefault(k => string.Equals(k.Name, secretName, StringComparison.Ordinal));

            Assert.Equal(OperationResult.Created, result.Result);
            Assert.Equal("TestSecretValue", result.Secret, StringComparer.Ordinal);
            Assert.NotNull(persistedSecrets);
            Assert.NotNull(newSecret);
            Assert.Equal(result.Secret, newSecret.Value);
            Assert.Equal(secretName, newSecret.Name, StringComparer.Ordinal);
            Assert.NotNull(persistedSecrets.MasterKey);
        }

        [Fact]
        public async Task SetMasterKey_WithProvidedKey_UsesProvidedKeyAndPersistsFile()
        {
            string testSecret = "abcde0123456789abcde0123456789abcde0123456789";
            using (var directory = new TempDirectory())
            {
                KeyOperationResult result;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false))
                {
                    result = await secretManager.SetMasterKeyAsync(testSecret);
                }

                bool functionSecretsExists = File.Exists(Path.Combine(directory.Path, "testfunction.json"));

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName));
                HostSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<HostSecrets>(secretsJson);

                Assert.NotNull(persistedSecrets);
                Assert.NotNull(persistedSecrets.MasterKey);
                Assert.Equal(OperationResult.Updated, result.Result);
                Assert.Equal(testSecret, result.Secret);
            }
        }

        [Fact]
        public async Task SetMasterKey_WithoutProvidedKey_GeneratesKeyAndPersistsFile()
        {
            using (var directory = new TempDirectory())
            {
                KeyOperationResult result;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: false))
                {
                    result = await secretManager.SetMasterKeyAsync();
                }

                bool functionSecretsExists = File.Exists(Path.Combine(directory.Path, "testfunction.json"));

                string secretsJson = File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName));
                HostSecrets persistedSecrets = ScriptSecretSerializer.DeserializeSecrets<HostSecrets>(secretsJson);

                Assert.NotNull(persistedSecrets);
                Assert.NotNull(persistedSecrets.MasterKey);
                Assert.Equal(OperationResult.Created, result.Result);
                Assert.Equal(result.Secret, persistedSecrets.MasterKey.Value);
            }
        }

        [Fact]
        public void Constructor_WithCreateHostSecretsIfMissingSet_CreatesHostSecret()
        {
            var secretsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var hostSecretPath = Path.Combine(secretsPath, ScriptConstants.HostMetadataFileName);
            try
            {
                string expectedTraceMessage = Resources.TraceHostSecretGeneration;
                bool preExistingFile = File.Exists(hostSecretPath);

                Mock<IKeyValueConverterFactory> mockValueConverterFactory = GetConverterFactoryMock(false, false);

                var secretManager = CreateSecretManager(secretsPath, createHostSecretsIfMissing: true, simulateWriteConversion: false, setStaleValue: false);
                bool fileCreated = File.Exists(hostSecretPath);

                Assert.False(preExistingFile);
                Assert.True(fileCreated);
            }
            finally
            {
                Directory.Delete(secretsPath, true);
            }
        }

        [Fact]
        public async Task GetHostSecrets_WhenNonDecryptedHostSecrets_SavesAndRefreshes()
        {
            using (var directory = new TempDirectory())
            {
                string expectedTraceMessage = Resources.TraceNonDecryptedHostSecretRefresh;
                string hostSecretsJson =
                    @"{
    'masterKey': {
        'name': 'master',
        'value': 'cryptoError',
        'encrypted': true
    },
    'functionKeys': [],
    'systemKeys': []
}";
                File.WriteAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName), hostSecretsJson);
                HostSecretsInfo hostSecrets;
                using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: true, setStaleValue: false))
                {
                    hostSecrets = await secretManager.GetHostSecretsAsync();
                }

                Assert.NotNull(hostSecrets);
                Assert.NotEqual(hostSecrets.MasterKey, "cryptoError");
                var result = JsonConvert.DeserializeObject<HostSecrets>(File.ReadAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName)));
                Assert.Equal(result.MasterKey.Value, "!" + hostSecrets.MasterKey);
                Assert.Equal(1, Directory.GetFiles(directory.Path, $"host.{ScriptConstants.Snapshot}*").Length);
            }
        }

        [Fact]
        public async Task GetFunctiontSecrets_WhenNonDecryptedSecrets_SavesAndRefreshes()
        {
            string key = TestHelpers.GenerateKeyHexString();
            using (new TestScopedEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey, key))
            {
                using (var directory = new TempDirectory())
                {
                    string functionName = "testfunction";
                    string expectedTraceMessage = string.Format(Resources.TraceNonDecryptedFunctionSecretRefresh, functionName, string.Empty);
                    string functionSecretsJson =
                         @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'cryptoError',
            'encrypted': true
        },
        {
            'name': 'Key2',
            'value': '1234',
            'encrypted': false
        }
    ]
}";
                    File.WriteAllText(Path.Combine(directory.Path, functionName + ".json"), functionSecretsJson);
                    IDictionary<string, string> functionSecrets;
                    using (var secretManager = CreateSecretManager(directory.Path, simulateWriteConversion: true, setStaleValue: false))
                    {
                            functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                    }

                    Assert.NotNull(functionSecrets);
                    Assert.NotEqual(functionSecrets["Key1"], "cryptoError");
                    var result = JsonConvert.DeserializeObject<FunctionSecrets>(File.ReadAllText(Path.Combine(directory.Path, functionName + ".json")));
                    Assert.Equal(result.GetFunctionKey("Key1", functionName).Value, "!" + functionSecrets["Key1"]);
                    Assert.Equal(1, Directory.GetFiles(directory.Path, $"{functionName}.{ScriptConstants.Snapshot}*").Length);

                    result = JsonConvert.DeserializeObject<FunctionSecrets>(File.ReadAllText(Path.Combine(directory.Path, functionName + ".json")));
                    string snapShotFileName = Directory.GetFiles(directory.Path, $"{functionName}.{ScriptConstants.Snapshot}*")[0];
                    result = JsonConvert.DeserializeObject<FunctionSecrets>(File.ReadAllText(Path.Combine(directory.Path, snapShotFileName)));
                    Assert.NotEqual(result.DecryptionKeyId, key);
                }
            }
        }

        [Fact]
        public async Task GetHostSecrets_WhenTooManyBackups_ThrowsException()
        {
            using (var directory = new TempDirectory())
            {
                string functionName = "testfunction";
                string expectedTraceMessage = string.Format(Resources.ErrorTooManySecretBackups, ScriptConstants.MaximumSecretBackupCount, functionName,
                    string.Format(Resources.ErrorSameSecrets, "test0,test1"));
                string functionSecretsJson =
                     @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'cryptoError',
            'encrypted': true
        },
        {
            'name': 'Key2',
            'value': 'FunctionValue2',
            'encrypted': false
        }
    ]
}";
                ILoggerFactory loggerFactory = new LoggerFactory();
                TestLoggerProvider loggerProvider = new TestLoggerProvider();
                loggerFactory.AddProvider(loggerProvider);
                var logger = loggerFactory.CreateLogger(LogCategories.CreateFunctionCategory("Test1"));
                IDictionary<string, string> functionSecrets;

                using (var secretManager = CreateSecretManager(directory.Path, logger))
                {
                    InvalidOperationException ioe = null;
                    try
                    {
                        for (int i = 0; i < ScriptConstants.MaximumSecretBackupCount + 20; i++)
                        {
                            File.WriteAllText(Path.Combine(directory.Path, functionName + ".json"), functionSecretsJson);

                            // If we haven't hit the exception yet, pause to ensure the file contents are being flushed.
                            if (i >= ScriptConstants.MaximumSecretBackupCount)
                            {
                                await Task.Delay(500);
                            }

                            // reset hostname provider and set a new hostname to force another backup
                            _hostNameProvider.Reset();
                            string hostName = "test" + (i % 2).ToString();
                            _testEnvironment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName, hostName);

                            functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        ioe = ex;
                    }
                }

                int backupCount = Directory.GetFiles(directory.Path, $"{functionName}.{ScriptConstants.Snapshot}*").Length;
                Assert.True(backupCount >= ScriptConstants.MaximumSecretBackupCount);
                Assert.True(loggerProvider.GetAllLogMessages().Any(
                    t => t.Level == LogLevel.Debug && t.FormattedMessage.IndexOf(expectedTraceMessage, StringComparison.OrdinalIgnoreCase) > -1),
                    "Expected Trace message not found");
            }
        }

        [Fact]
        public async Task GetHostSecretsAsync_WaitsForNewSecrets()
        {
            using (var directory = new TempDirectory())
            {
                string hostSecretsJson = @"{
    'masterKey': {
        'name': 'master',
        'value': '1234',
        'encrypted': false
    },
    'functionKeys': [],
    'systemKeys': []
}";
                string filePath = Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName);
                File.WriteAllText(filePath, hostSecretsJson);

                HostSecretsInfo hostSecrets = null;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    await Task.WhenAll(
                        Task.Run(async () =>
                        {
                            // Lock the file
                            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                            {
                                await Task.Delay(500);
                            }
                        }),
                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            hostSecrets = await secretManager.GetHostSecretsAsync();
                        }));

                    Assert.Equal(hostSecrets.MasterKey, "1234");
                }

                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    await Assert.ThrowsAsync<IOException>(async () =>
                    {
                        await Task.WhenAll(
                            Task.Run(async () =>
                            {
                                // Lock the file
                                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                                {
                                    await Task.Delay(3000);
                                }
                            }),
                            Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                hostSecrets = await secretManager.GetHostSecretsAsync();
                            }));
                    });
                }
            }
        }

        [Fact]
        public async Task GetFunctionSecretsAsync_WaitsForNewSecrets()
        {
            using (var directory = new TempDirectory())
            {
                string functionName = "testfunction";
                string functionSecretsJson =
                 @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'FunctionValue1',
            'encrypted': false
        },
        {
            'name': 'Key2',
            'value': 'FunctionValue2',
            'encrypted': false
        }
    ]
}";
                string filePath = Path.Combine(directory.Path, functionName + ".json");
                File.WriteAllText(filePath, functionSecretsJson);

                IDictionary<string, string> functionSecrets = null;
                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    await Task.WhenAll(
                        Task.Run(async () =>
                        {
                            // Lock the file
                            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                            {
                                await Task.Delay(500);
                            }
                        }),
                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                        }));

                    Assert.Equal(functionSecrets["Key1"], "FunctionValue1");
                }

                using (var secretManager = CreateSecretManager(directory.Path))
                {
                    await Assert.ThrowsAsync<IOException>(async () =>
                    {
                        await Task.WhenAll(
                            Task.Run(async () =>
                            {
                                // Lock the file
                                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
                                {
                                    await Task.Delay(3000);
                                }
                            }),
                            Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                            }));
                    });
                }
            }
        }

        [Fact]
        public async Task GetHostSecrets_AddMetrics()
        {
            using (var directory = new TempDirectory())
            {
                string expectedTraceMessage = Resources.TraceNonDecryptedHostSecretRefresh;
                string hostSecretsJson =
                    @"{
    'masterKey': {
        'name': 'master',
        'value': 'cryptoError',
        'encrypted': true
    },
    'functionKeys': [],
    'systemKeys': []
}";
                File.WriteAllText(Path.Combine(directory.Path, ScriptConstants.HostMetadataFileName), hostSecretsJson);
                HostSecretsInfo hostSecrets;
                TestMetricsLogger metricsLogger = new TestMetricsLogger();

                using (var secretManager = CreateSecretManager(directory.Path, metricsLogger: metricsLogger, simulateWriteConversion: true, setStaleValue: false))
                {
                    hostSecrets = await secretManager.GetHostSecretsAsync();
                }

                string eventName = string.Format(MetricEventNames.SecretManagerGetHostSecrets, typeof(FileSystemSecretsRepository).Name.ToLower());
                metricsLogger.EventsBegan.Single(e => string.Equals(e, eventName));
                metricsLogger.EventsEnded.Single(e => string.Equals(e.ToString(), eventName));
            }
        }

        [Fact]
        public async Task GetFunctiontSecrets_AddsMetrics()
        {
            using (var directory = new TempDirectory())
            {
                string functionName = "testfunction";
                string functionSecretsJson =
                     @"{
    'keys': [
        {
            'name': 'Key1',
            'value': 'cryptoError',
            'encrypted': true
        },
        {
            'name': 'Key2',
            'value': '1234',
            'encrypted': false
        }
    ]
}";
                File.WriteAllText(Path.Combine(directory.Path, functionName + ".json"), functionSecretsJson);
                IDictionary<string, string> functionSecrets;
                TestMetricsLogger metricsLogger = new TestMetricsLogger();

                using (var secretManager = CreateSecretManager(directory.Path, metricsLogger: metricsLogger, simulateWriteConversion: true, setStaleValue: false))
                {
                    functionSecrets = await secretManager.GetFunctionSecretsAsync(functionName);
                }

                string eventName = string.Format(MetricEventNames.SecretManagerGetFunctionSecrets, typeof(FileSystemSecretsRepository).Name.ToLower());
                metricsLogger.EventsBegan.Single(e => e.StartsWith(eventName));
                metricsLogger.EventsBegan.Single(e => e.Contains("testfunction"));
                metricsLogger.EventsEnded.Single(e => e.ToString().StartsWith(eventName));
                metricsLogger.EventsEnded.Single(e => e.ToString().Contains("testfunction"));
            }
        }

        private Mock<IKeyValueConverterFactory> GetConverterFactoryMock(bool simulateWriteConversion = true, bool setStaleValue = true)
        {
            var mockValueReader = new Mock<IKeyValueReader>();
            mockValueReader.Setup(r => r.ReadValue(It.IsAny<Key>()))
                .Returns<Key>(k =>
                {
                    if (k.Value.StartsWith("cryptoError"))
                    {
                        throw new CryptographicException();
                    }
                    return new Key(k.Name, k.Value) { IsStale = setStaleValue ? true : k.IsStale };
                });

            var mockValueWriter = new Mock<IKeyValueWriter>();
            mockValueWriter.Setup(r => r.WriteValue(It.IsAny<Key>()))
                .Returns<Key>(k =>
                {
                    return new Key(k.Name, simulateWriteConversion ? "!" + k.Value : k.Value) { IsEncrypted = simulateWriteConversion };
                });

            var mockValueConverterFactory = new Mock<IKeyValueConverterFactory>();
            mockValueConverterFactory.Setup(f => f.GetValueReader(It.IsAny<Key>()))
                .Returns(mockValueReader.Object);
            mockValueConverterFactory.Setup(f => f.GetValueWriter(It.IsAny<Key>()))
                .Returns(mockValueWriter.Object);

            return mockValueConverterFactory;
        }

        private SecretManager CreateSecretManager(string secretsPath, ILogger logger = null, IMetricsLogger metricsLogger = null, IKeyValueConverterFactory keyConverterFactory = null, bool createHostSecretsIfMissing = false, bool simulateWriteConversion = true, bool setStaleValue = true)
        {
            logger = logger ?? NullLogger.Instance;
            metricsLogger = metricsLogger ?? new TestMetricsLogger();

            if (keyConverterFactory == null)
            {
                Mock<IKeyValueConverterFactory> mockValueConverterFactory = GetConverterFactoryMock(simulateWriteConversion, setStaleValue);
                keyConverterFactory = mockValueConverterFactory.Object;
            }

            ISecretsRepository repository = new FileSystemSecretsRepository(secretsPath);
            return new SecretManager(repository, keyConverterFactory, logger, metricsLogger, _hostNameProvider, _startupContextProvider, createHostSecretsIfMissing);
        }
    }
}
