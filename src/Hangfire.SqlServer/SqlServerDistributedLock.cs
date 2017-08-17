// This file is part of Hangfire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using Dapper;
using Hangfire.Annotations;
using Hangfire.Storage;

namespace Hangfire.SqlServer
{
    public class SqlServerDistributedLock : IDisposable
    {
        private static readonly TimeSpan MaxAttemptDelay = TimeSpan.FromSeconds(5);

        private const string LockMode = "Exclusive";
        private const string LockOwner = "Session";
        private const int CommandTimeoutAdditionSeconds = 1;

        // Connections to SQL Azure Database that are idle for 30 minutes 
        // or longer will be terminated. And since we are using separate
        // connection for a distributed lock, we'd like to prevent Resource
        // Governor from terminating it.
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(1);

        private static readonly IDictionary<int, string> LockErrorMessages
            = new Dictionary<int, string>
            {
                { -1, "The lock request timed out" },
                { -2, "The lock request was canceled" },
                { -3, "The lock request was chosen as a deadlock victim" },
                { -999, "Indicates a parameter validation or other call error" }
            };

        private static readonly ThreadLocal<Dictionary<string, int>> AcquiredLocks
            = new ThreadLocal<Dictionary<string, int>>(() => new Dictionary<string, int>()); 

        private IDbConnection _connection;
        private readonly SqlServerStorage _storage;
        private readonly string _resource;
        private readonly Timer _timer;
        private readonly object _lockObject = new object();

        private bool _completed;

        public SqlServerDistributedLock([NotNull] SqlServerStorage storage, [NotNull] string resource, TimeSpan timeout)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (String.IsNullOrEmpty(resource)) throw new ArgumentNullException(nameof(resource));
            if (timeout.TotalSeconds + CommandTimeoutAdditionSeconds > Int32.MaxValue) throw new ArgumentException(
                $"The timeout specified is too large. Please supply a timeout equal to or less than {Int32.MaxValue - CommandTimeoutAdditionSeconds} seconds", nameof(timeout));
            if (timeout.TotalMilliseconds > Int32.MaxValue) throw new ArgumentException(
                $"The timeout specified is too large. Please supply a timeout equal to or less than {(int)TimeSpan.FromMilliseconds(Int32.MaxValue).TotalSeconds} seconds", nameof(timeout));
                
            _storage = storage;
            _resource = resource;

            if (!AcquiredLocks.Value.ContainsKey(_resource) || AcquiredLocks.Value[_resource] == 0)
            {
                _connection = storage.CreateAndOpenConnection();

                try
                {
                    Acquire(_connection, _resource, timeout);
                }
                catch (Exception)
                {
                    storage.ReleaseConnection(_connection);
                    throw;
                }

                if (!_storage.IsExistingConnection(_connection))
                {
                    _timer = new Timer(ExecuteKeepAliveQuery, null, KeepAliveInterval, KeepAliveInterval);
                }

                AcquiredLocks.Value[_resource] = 1;
            }
            else
            {
                AcquiredLocks.Value[_resource]++;
            }
        }

        public void Dispose()
        {
            if (_completed) return;

            _completed = true;

            if (!AcquiredLocks.Value.ContainsKey(_resource)) return;

            AcquiredLocks.Value[_resource]--;

            if (AcquiredLocks.Value[_resource] != 0) return;

            lock (_lockObject)
            {
                // Timer callback may be invoked after the Dispose method call,
                // so we are using lock to avoid unsynchronized calls.

                try
                {
                    AcquiredLocks.Value.Remove(_resource);

                    _timer?.Dispose();

                    if (_connection.State == ConnectionState.Open)
                    {
                        // Session-scoped application locks are held only when connection
                        // is open. When connection is closed or broken, for example, when
                        // there was an error, application lock is already released by SQL
                        // Server itself, and we shouldn't do anything.
                        Release(_connection, _resource);
                    }
                }
                finally
                {
                    _storage.ReleaseConnection(_connection);
                    _connection = null;
                }
            }
        }

        private void ExecuteKeepAliveQuery(object obj)
        {
            lock (_lockObject)
            {
                try
                {
                    _connection?.Execute("SELECT 1;");
                }
                catch
                {
                    // Connection is broken. This means that distributed lock
                    // was released, and we can't guarantee the safety property
                    // for the code that is wrapped with this block. So it was
                    // a bad idea to have a separate connection for just
                    // distributed lock.
                    // TODO: Think about distributed locks and connections.
                }
            }
        }

        internal static void Acquire(IDbConnection connection, string resource, TimeSpan timeout)
        {
            if (connection.State != ConnectionState.Open)
            {
                // When we are passing a closed connection to Dapper's Execute method,
                // it kindly opens it for us, but after command execution, it will be closed
                // automatically, and our just-acquired application lock will immediately
                // be released. This is not behavior we want to achieve, so let's throw an
                // exception instead.
                throw new InvalidOperationException("Connection must be open before acquiring a distributed lock.");
            }

            var started = Stopwatch.StartNew();
            var attempt = 1;

            while (started.Elapsed < timeout)
            {
                var parameters = new DynamicParameters();
                parameters.Add("@Resource", resource);
                parameters.Add("@DbPrincipal", "public");
                parameters.Add("@LockMode", LockMode);
                parameters.Add("@LockOwner", LockOwner);
                parameters.Add("@LockTimeout", 0);
                parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

                connection.Execute(
                    @"sp_getapplock",
                    parameters,
                    commandTimeout: (int)timeout.TotalSeconds,
                    commandType: CommandType.StoredProcedure);

                var lockResult = parameters.Get<int>("@Result");

                if (lockResult >= 0)
                {
                    // The lock has been successfully obtained on the specified resource.
                    return;
                }

                if (lockResult == -999 /* Indicates a parameter validation or other call error. */)
                {
                    throw new SqlServerDistributedLockException(
                        $"Could not place a lock on the resource '{resource}': {(LockErrorMessages.ContainsKey(lockResult) ? LockErrorMessages[lockResult] : $"Server returned the '{lockResult}' error.")}.");
                }

                Thread.Sleep(ExponentialBackoff(attempt++));
            }

            throw new DistributedLockTimeoutException(resource);
        }

        internal static void Release(IDbConnection connection, string resource)
        {
            var parameters = new DynamicParameters();
            parameters.Add("@Resource", resource);
            parameters.Add("@LockOwner", LockOwner);
            parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            connection.Execute(
                @"sp_releaseapplock",
                parameters,
                commandType: CommandType.StoredProcedure);

            var releaseResult = parameters.Get<int>("@Result");

            if (releaseResult < 0)
            {
                throw new SqlServerDistributedLockException(
                    $"Could not release a lock on the resource '{resource}': Server returned the '{releaseResult}' error.");
            }
        }

        private static TimeSpan ExponentialBackoff(int attemptNumber)
        {
            var rand = new Random(Guid.NewGuid().GetHashCode());
            var nextTry = rand.Next(
                (int)Math.Pow(attemptNumber, 2), (int)Math.Pow(attemptNumber + 1, 2) + 1);
            return TimeSpan.FromMilliseconds(Math.Min(nextTry, MaxAttemptDelay.TotalMilliseconds));
        }
    }
}
