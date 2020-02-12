using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Couchbase.Core.DI;
using Couchbase.Core.IO.Operations;
using Microsoft.Extensions.Logging;
using Exception = System.Exception;

#nullable enable

namespace Couchbase.Core.IO.Connections.DataFlow
{
    /// <summary>
    /// Connection pool based on queuing operations via the TPL data flows library.
    /// </summary>
    internal class DataFlowConnectionPool : ConnectionPoolBase
    {
        private readonly ILogger<DataFlowConnectionPool> _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1);

        private readonly List<(IConnection Connection, ActionBlock<SendOperationRequest> Block)> _connections =
            new List<(IConnection Connection, ActionBlock<SendOperationRequest> Block)>();

        private readonly BufferBlock<SendOperationRequest> _sendQueue =
            new BufferBlock<SendOperationRequest>(new DataflowBlockOptions
            {
                BoundedCapacity = 1024
            });

        private bool _initialized;

        /// <summary>
        /// Minimum number of connections in the pool.
        /// </summary>
        public int MinimumSize { get; set; } = 2;

        /// <summary>
        /// Maximum number of connections in the pool.
        /// </summary>
        public int MaximumSize { get; set; } = 5;

        /// <summary>
        /// Creates a new DataFlowConnectionPool.
        /// </summary>
        /// <param name="connectionInitializer">Handler for initializing new connections.</param>
        /// <param name="connectionFactory">Factory for creating new connections.</param>
        /// <param name="logger">Logger.</param>
        public DataFlowConnectionPool(IConnectionInitializer connectionInitializer, IConnectionFactory connectionFactory,
            ILogger<DataFlowConnectionPool> logger)
            : base(connectionInitializer, connectionFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public override async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
            {
                return;
            }

            await AddConnectionsAsync(MinimumSize, cancellationToken);

            _logger.LogDebug("Connection pool for {endpoint} initialized with {size} connections.", EndPoint, MinimumSize);

            _initialized = true;
        }

        /// <inheritdoc />
        public override Task SendAsync(IOperation operation, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            var operationRequest = new SendOperationRequest(operation, cancellationToken);

            if (_connections.Count > 0)
            {
                _sendQueue.Post(operationRequest);

                return operationRequest.CompletionTask;
            }

            // We had all connections die earlier and fail to restart, we need to restart them
            return CleanupDeadConnectionsAsync().ContinueWith(_ =>
            {
                if (!_cts.IsCancellationRequested)
                {
                    // Requeue the request
                    // Note: always requeues even if cleanup fails
                    // Since the exception on the task is ignored, we're also eating the exception

                    _sendQueue.Post(operationRequest);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public override IEnumerable<IConnection> GetConnections()
        {
            EnsureNotDisposed();

            return new List<IConnection>(_connections.Select(p => p.Connection));
        }

        /// <inheritdoc />
        protected override async ValueTask<IAsyncDisposable> FreezePoolAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new FreezeDisposable(this);
        }

        private void EnsureNotDisposed()
        {
            if (_cts.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(DataFlowConnectionPool));
            }
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel(false);

            // Take out a lock to prevent more connections from opening while we're disposing
            // Don't need to release
            _lock.Wait();
            try
            {
                // Complete any queued commands
                _sendQueue.Complete();
                _sendQueue.Completion.GetAwaiter().GetResult();

                // Dispose of the connections
                foreach (var connection in _connections)
                {
                    connection.Connection.Dispose();
                }

                _connections.Clear();
            }
            finally
            {
                _lock.Dispose();
                _cts.Dispose();
            }
        }

        #region Connection Management

        /// <summary>
        /// Adds a certain number of connections to the pool. Assumes that the pool is already locked.
        /// </summary>
        /// <param name="count">Number of connections to add.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// This method will fail if the total number of requested connections could not be added.
        /// However, it may have partially succeeded, some connections may have been added.
        /// </remarks>
        private async Task AddConnectionsAsync(int count, CancellationToken cancellationToken = default)
        {
            if (count <= 0)
            {
                return;
            }

            async Task StartConnection()
            {
                // Create and initialize a new connection
                var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Create an ActionBlock to receive messages for this connection
                var block = new ActionBlock<SendOperationRequest>(BuildHandler(connection),
                    new ExecutionDataflowBlockOptions
                    {
                        BoundedCapacity = 1, // Don't let the action block queue up requests, they should queue in the buffer block
                        MaxDegreeOfParallelism = 1, // Each connection can only process one send at a time
                        SingleProducerConstrained = true // Can provide better performance since we know only
                    });

                // Receive messages from the queue
                _sendQueue.LinkTo(block, new DataflowLinkOptions
                {
                    PropagateCompletion = true
                });

                lock (_connections)
                {
                    // As each connection is successful, add it to our list of connections
                    // This way if 4 succeed and 1 fails, the 4 that succeed are still up and available
                    // We need an additional lock here because _connections.Add might get called
                    // simultaneously as each connection is successfully started, but this is a different
                    // lock from the preexisting lock on the overall pool using _lock.

                    _connections.Add((connection, block));
                }
            }

            // Startup connections up to the minimum pool size in parallel
            var tasks =
                Enumerable.Range(1, count)
                    .Select(p => StartConnection())
                    .ToList();

            // Wait for all connections to be started
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a SendOperationRequest handler for a specific connection.
        /// </summary>
        /// <param name="connection">The connection.</param>
        /// <returns>The handler.</returns>
        private Func<SendOperationRequest, Task> BuildHandler(IConnection connection)
        {
            return request =>
            {
                if (connection.IsDead)
                {
                    // We need to return the task from CleanupDeadConnectionsAsync
                    // Because as long as the task is not completed, this connection won't
                    // receive more requests. We need to wait until the dead connection is
                    // unlinked to make sure no more bad requests hit it.
                    return CleanupDeadConnectionsAsync().ContinueWith(_ =>
                    {
                        if (!_cts.IsCancellationRequested)
                        {
                            // Requeue the request for a different connection
                            // Note: always requeues even if cleanup fails
                            // Since the exception on the task is ignored, we're also eating the exception

                            _sendQueue.Post(request);
                        }
                    }, _cts.Token);
                }

                return request.SendAsync(connection);
            };
        }

        /// <summary>
        /// Locks the collection, removes any dead connections, and replaces them.
        /// </summary>
        /// <returns></returns>
        private async Task CleanupDeadConnectionsAsync()
        {
            await _lock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var deadCount = 0;

                for (var i = 0; i < _connections.Count; i++)
                {
                    if (_connections[i].Connection.IsDead)
                    {
                        _connections[i].Block.Complete();
                        _connections[i].Connection.Dispose();
                        _connections.RemoveAt(i);

                        deadCount++;
                        i--;
                    }
                }

                if (deadCount > 0)
                {
                    _logger.LogInformation("Connection pool for {endpoint} has {size} dead connections, removing.",
                        EndPoint, deadCount);
                }

                // Ensure that we still meet the minimum size
                var needToRestart = MinimumSize - _connections.Count;
                if (needToRestart > 0)
                {
                    try
                    {
                        await AddConnectionsAsync(needToRestart, _cts.Token).ConfigureAwait(false);

                        _logger.LogInformation("Restarted {size} connections for {endpoint}.", needToRestart, EndPoint);
                    }
                    catch (Exception ex)
                    {
                        // Eat the error if we were unable to restart one of the dead connections, but log
                        _logger.LogError(ex, "Error replacing dead connections for {endpoint}.", EndPoint);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        #endregion

        private class FreezeDisposable : IAsyncDisposable
        {
            private readonly DataFlowConnectionPool _connectionPool;

            public FreezeDisposable(DataFlowConnectionPool connectionPool)
            {
                _connectionPool = connectionPool;
            }

            public ValueTask DisposeAsync()
            {
                _connectionPool._lock.Release();

                return default;
            }
        }
    }
}
