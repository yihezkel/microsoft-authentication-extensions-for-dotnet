﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace Microsoft.Identity.Client.Extensions.Msal
{

    /// <summary>
    /// Persist cache to file
    /// </summary>
    internal class MsalCacheStorage
    {
        private readonly TraceSourceLogger _logger;

        private readonly ICacheAccessor _cacheAccessor;

        internal StorageCreationProperties CreationProperties { get; }

        internal const string PersistenceValidationDummyData = "msal_persistence_test";



        /// <summary>
        /// A default logger for use if the user doesn't want to provide their own.
        /// </summary>
        private static readonly Lazy<TraceSourceLogger> s_staticLogger = new Lazy<TraceSourceLogger>(() =>
        {
            return new TraceSourceLogger(EnvUtils.GetNewTraceSource(nameof(MsalCacheHelper) + "Singleton"));
        });

        /// <summary>
        /// Initializes a new instance of the <see cref="MsalCacheStorage"/> class.
        /// The actual cache reading and writing is OS specific:
        /// <list type="bullet">
        /// <item>
        ///     <term>Windows</term>
        ///     <description>DPAPI encyrpted file on behalf of the user. </description>
        /// </item>
        /// <item>
        ///     <term>Mac</term>
        ///     <description>Cache is stored in KeyChain.  </description>
        /// </item>
        /// <item>
        ///     <term>Linux</term>
        ///     <description>Cache is stored in Gnone KeyRing - https://developer.gnome.org/libsecret/0.18/  </description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="creationProperties">Properties for creating the cache storage on disk</param>
        /// <param name="logger">logger</param>
        /// <returns></returns>
        public static MsalCacheStorage Create(StorageCreationProperties creationProperties, TraceSource logger = null)
        {
            TraceSourceLogger actualLogger = logger == null ? s_staticLogger.Value : new TraceSourceLogger(logger);

            ICacheAccessor cacheAccessor;
            if (SharedUtilities.IsWindowsPlatform())
            {
                cacheAccessor = new CacheAccessorWindows(creationProperties.CacheFilePath, actualLogger);
            }
            else if (SharedUtilities.IsMacPlatform())
            {
                cacheAccessor = new CacheAccessorMac(
                    creationProperties.CacheFilePath,
                    creationProperties.MacKeyChainServiceName,
                    creationProperties.MacKeyChainAccountName,
                    actualLogger);
            }
            else if (SharedUtilities.IsLinuxPlatform())
            {
                cacheAccessor = new CacheAccessorLinux(
                   creationProperties.CacheFilePath,
                   creationProperties.KeyringCollection,
                   creationProperties.KeyringSchemaName,
                   creationProperties.KeyringSecretLabel,
                   creationProperties.KeyringAttribute1.Key,
                   creationProperties.KeyringAttribute1.Value,
                   creationProperties.KeyringAttribute2.Key,
                   creationProperties.KeyringAttribute2.Value,
                   actualLogger);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            return new MsalCacheStorage(creationProperties, cacheAccessor, actualLogger);
        }

        internal /* internal for test, otherwise private */ MsalCacheStorage(
            StorageCreationProperties creationProperties,
            ICacheAccessor cacheAccessor,
            TraceSourceLogger logger)
        {
            CreationProperties = creationProperties;
            _logger = logger;
            _cacheAccessor = cacheAccessor;
            _logger.LogInformation($"Initialized '{nameof(MsalCacheStorage)}'");
        }

        /// <summary>
        /// Gets cache file path
        /// </summary>
        public string CacheFilePath => CreationProperties.CacheFilePath;

        /// <summary>
        /// Gets a value indicating whether the persisted file has changed since we last read it.
        /// </summary>
        public bool HasChanged
        {
            get
            {
                // Attempts to make this more refined have all resulted in some form of cache inconsistency. Just returning
                // true here so we always load from disk.
                return true;
            }
        }

        /// <summary>
        /// Read and unprotect cache data
        /// </summary>
        /// <returns>Unprotected cache data</returns>
        public byte[] ReadData()
        {
            bool cacheFileExists = File.Exists(CacheFilePath);
            _logger.LogInformation($"ReadData Cache file exists '{cacheFileExists}'");

            byte[] data = null;
            try
            {
                _logger.LogInformation($"Reading Data");
                data = _cacheAccessor.Read();
                _logger.LogInformation($"Got '{data?.Length}' bytes from file storage");
            }
            catch (Exception e)
            {
                _logger.LogError($"An exception was encountered while reading data from the {nameof(MsalCacheStorage)} : {e}");

                // It's unlikely that Clear will work, but try it anyway
                Clear();
            }

            return data ?? new byte[0];
        }

        /// <summary>
        /// Protect and write cache data to file
        /// </summary>
        /// <param name="data">Cache data</param>
        public void WriteData(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            try
            {
                _logger.LogInformation($"Got '{data?.Length}' bytes to write to storage");
                _cacheAccessor.Write(data);
            }
            catch (Exception e)
            {
                _logger.LogError($"An exception was encountered while writing data to {nameof(MsalCacheStorage)} : {e}");
            }
        }

        /// <summary>
        /// Delete cache file
        /// </summary>
        public void Clear()
        {
            try
            {
                _logger.LogInformation("Clearing the cache file");
                _cacheAccessor.Clear();
            }
            catch (Exception e)
            {
                _logger.LogError($"An exception was encountered while clearing data from {nameof(MsalCacheStorage)} : {e}");
            }
        }

        public void VerifyPersistence()
        {
            // do not use the _cacheAccessor for writing dummy data, as it might overwrite an actual token cache
            var persitenceValidatationAccessor = _cacheAccessor.CreateForPersistenceValidation();

            try
            {
                _logger.LogInformation($"[Verify Persistence] Writing Data ");
                persitenceValidatationAccessor.Write(Encoding.UTF8.GetBytes(PersistenceValidationDummyData));

                _logger.LogInformation($"[Verify Persistence] Reading Data ");
                var data = persitenceValidatationAccessor.Read();

                if (data == null || data.Length == 0)
                {
                    throw new MsalCachePersistenceException(
                        "Persistence check failed. Data was written but it could not be read. " +
                        "Possible cause: on Linux, LibSecret is installed but D-Bus isn't running because it cannot be started over SSH.");
                }

                string dataRead = Encoding.UTF8.GetString(data);
                if (!string.Equals(PersistenceValidationDummyData, dataRead, StringComparison.Ordinal))
                {
                    throw new MsalCachePersistenceException(
                        $"Persistence check failed. Data written {PersistenceValidationDummyData} is different from data read {dataRead}");
                }
            }
            catch (Exception ex) when (!(ex is MsalCachePersistenceException))
            {
                throw new MsalCachePersistenceException("Persistence check failed. Inspect inner exception for details", ex);
            }
            finally
            {
                try
                {
                    _logger.LogInformation($"[Verify Persistence] Clearing data");
                    persitenceValidatationAccessor.Clear();
                }
                catch (Exception e)
                {
                    _logger.LogError($"[Verify Persistence] Could not clear the test data: " + e);
                }
            }
        }
    }
}
