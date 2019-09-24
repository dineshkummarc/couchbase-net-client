using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core;
using Couchbase.Core.IO;
using Couchbase.Core.IO.Converters;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.IO.Operations.Legacy;
using Couchbase.Core.IO.Operations.Legacy.SubDocument;
using Couchbase.Core.IO.Operations.SubDocument;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Core.Logging;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;
using Couchbase.Core.Sharding;

namespace Couchbase.Services.KeyValue
{
    public class CouchbaseCollection : ICollection, IBinaryCollection
    {
        internal const string DefaultCollectionName = "_default";
        private static readonly ILogger Log = LogManager.CreateLogger<CouchbaseCollection>();
        private readonly BucketBase _bucket;
        private static readonly TimeSpan DefaultTimeout = new TimeSpan(0,0,0,0,2500);//temp
        private readonly ITypeTranscoder _transcoder = new DefaultTranscoder(new DefaultConverter());

        internal CouchbaseCollection(BucketBase bucket, uint? cid, string name)
        {
            Cid = cid;
            Name = name;
            _bucket = bucket;
        }

        public uint? Cid { get; }

        public string Name { get; }

        public IBinaryCollection Binary => this;

        private static Exception ThrowException(SocketAsyncState state)
        {
            var statusName = Enum.GetName(typeof(ResponseStatus), state.Status);
            switch (state.Status)
            {
                case ResponseStatus.KeyNotFound:
                    return new KeyNotFoundException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.KeyExists:
                    return new KeyExistsException(statusName,new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.ValueTooLarge:
                    return new ValueTooLargeException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.InvalidArguments:
                    return new InvalidArgumentException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.TemporaryFailure:
                case ResponseStatus.OutOfMemory:
                case ResponseStatus.Busy:
                    return new TempFailException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.OperationTimeout:
                    return new TimeoutException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.Locked:
                    return new KeyLockedException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.DocumentMutationLost:
                case ResponseStatus.DocumentMutationDetected:
                case ResponseStatus.NoReplicasFound:
                case ResponseStatus.DurabilityInvalidLevel:
                case ResponseStatus.DurabilityImpossible:
                case ResponseStatus.SyncWriteInProgress:
                case ResponseStatus.SyncWriteAmbiguous:
                    return new DurabilityException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.Eaccess:
                case ResponseStatus.AuthenticationError:
                    return new AuthenticationException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                //internal errors handled by the app?
                case ResponseStatus.Rollback:
                case ResponseStatus.VBucketBelongsToAnotherServer:
                case ResponseStatus.AuthenticationContinue:
                case ResponseStatus.AuthStale:
                case ResponseStatus.InternalError:
                case ResponseStatus.UnknownCommand:
                case ResponseStatus.BucketNotConnected:
                case ResponseStatus.UnknownError:
                case ResponseStatus.NotInitialized:
                case ResponseStatus.NotSupported:
                case ResponseStatus.SubdocXattrUnknownVattr:
                case ResponseStatus.SubDocMultiPathFailure:
                case ResponseStatus.SubDocXattrInvalidFlagCombo:
                case ResponseStatus.SubDocXattrInvalidKeyCombo:
                case ResponseStatus.SubdocXattrCantModifyVattr:
                case ResponseStatus.SubdocMultiPathFailureDeleted:
                case ResponseStatus.SubdocInvalidXattrOrder:
                    return new InternalErrorException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.InvalidRange:
                case ResponseStatus.ItemNotStored:
                case ResponseStatus.IncrDecrOnNonNumericValue:
                    return new KeyValueException //hmm?
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    };
                //sub doc errors
                case ResponseStatus.SubDocPathNotFound:
                    return new PathNotFoundException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.SubDocPathMismatch:
                    return new PathMismatchException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.SubDocPathInvalid:
                    return new PathInvalidException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.SubDocPathTooBig:
                    return new PathTooBigException(statusName, new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    });
                case ResponseStatus.SubDocDocTooDeep:
                case ResponseStatus.SubDocCannotInsert:
                case ResponseStatus.SubDocDocNotJson:
                case ResponseStatus.SubDocNumRange:
                case ResponseStatus.SubDocDeltaRange:
                case ResponseStatus.SubDocPathExists:
                case ResponseStatus.SubDocValueTooDeep:
                case ResponseStatus.SubDocInvalidCombo:
                case ResponseStatus.SubdocXattrUnknownMacro:
                    return new KeyValueException
                    {
                        Status = state.Status,
                        ErrorMap = state.ErrorMap
                    };
                //remove these ones
                case ResponseStatus.Failure:
                case ResponseStatus.ClientFailure:
                    break;
                case ResponseStatus.NodeUnavailable:
                    break;
                case ResponseStatus.TransportFailure:
                    return state.Exception;
                default:
                    return new ArgumentOutOfRangeException();
            }
            return new Exception("oh me oh mai...");
        }

        #region ExecuteOp Helper

        private async Task ExecuteOp(IOperation op, CancellationToken token = default(CancellationToken), TimeSpan? timeout = null)
        {
            Log.LogDebug("Executing op {0} with key {1} and opaque {2}", op.OpCode, op.Key, op.Opaque);

            // wire up op's completed function
            var tcs = new TaskCompletionSource<IMemoryOwner<byte>>();
            op.Completed = state =>
            {
                if (state.Status == ResponseStatus.Success)
                {
                    tcs.TrySetResult(state.ExtractData());
                }
                else
                {
                    tcs.TrySetException(ThrowException(state));
                }

                return tcs.Task;
            };

            CancellationTokenSource cts = null;
            try
            {
                if (token == CancellationToken.None)
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(timeout.HasValue && timeout != TimeSpan.Zero ? timeout.Value : DefaultTimeout);
                    token = cts.Token;
                }

                using (token.Register(() =>
                {
                    if (tcs.Task.Status != TaskStatus.RanToCompletion)
                    {
                        tcs.TrySetCanceled();
                    }
                }, useSynchronizationContext: false))
                {
                    await _bucket.Send(op, tcs).ConfigureAwait(false);
                    var bytes = await tcs.Task.ConfigureAwait(false);
                    await op.ReadAsync(bytes).ConfigureAwait(false);

                    Log.LogDebug("Completed executing op {0} with key {1} and opaque {2}", op.OpCode, op.Key, op.Opaque);
                }
            }
            catch (OperationCanceledException e)
            {
                if (!e.CancellationToken.IsCancellationRequested)
                {
                    //oddly IsCancellationRequested is false when timed out
                    throw new TimeoutException();
                }
            }
            finally
            {
                //clean up the token if we used a default token
                cts?.Dispose();
            }
        }

        #endregion

        #region Get

        public async Task<IGetResult> GetAsync(string id, GetOptions options)
        {
            var specs = new List<OperationSpec>();
            if (options.IncludeExpiry)
            {
                specs.Add(new OperationSpec
                {
                    OpCode = OpCode.SubGet,
                    Path = VirtualXttrs.DocExpiryTime,
                    PathFlags = SubdocPathFlags.Xattr
                });
            }
            if (!options.Timeout.HasValue)
            {
                options.Timeout = DefaultTimeout;
            }

            var projectList = options.ProjectList;
            if (projectList.Any())
            {
                //we have succeeded the max #fields returnable by sub-doc so fetch the whole doc
                if (projectList.Count + specs.Count > 16)
                {
                    specs.Add(new OperationSpec
                    {
                        Path = "",
                        OpCode = OpCode.Get,
                        DocFlags = SubdocDocFlags.None
                    });
                }
                else
                {
                    //Add the projections for fetching
                    projectList.ForEach(path=>specs.Add(new OperationSpec
                    {
                        OpCode = OpCode.SubGet,
                        Path = path
                    }));
                }
            }
            else
            {
                //Project list is empty so fetch the whole doc
                specs.Add(new OperationSpec
                {
                    Path = "",
                    OpCode = OpCode.Get,
                    DocFlags = SubdocDocFlags.None
                });
            }

            var lookupOp = await ExecuteLookupIn(id,
                    specs, new LookupInOptions().WithTimeout(options.Timeout.Value))
                .ConfigureAwait(false);

            var transcoder = options.Transcoder ?? _transcoder;
            return new GetResult(lookupOp.ExtractData(), transcoder, specs)
            {
                Id = lookupOp.Key,
                Cas = lookupOp.Cas,
                OpCode = lookupOp.OpCode,
                Flags = lookupOp.Flags,
                Header = lookupOp.Header
            };
        }

        #endregion

        #region Exists

        public async Task<IExistsResult> ExistsAsync(string id, ExistsOptions options)
        {
            using (var existsOp = new Observe
            {
                Key = id,
                Cid = Cid,
                Transcoder = _transcoder
            })
            {
                try
                {
                    await ExecuteOp(existsOp, options.Token, options.Timeout);
                    var value = existsOp.GetValue();
                    return new ExistsResult
                    {
                        Exists = existsOp.Success && value.KeyState != KeyState.NotFound &&
                                 value.KeyState != KeyState.LogicalDeleted,
                        Cas = value.Cas,
                        Expiry = TimeSpan.FromMilliseconds(existsOp.Expires)
                    };
                }
                catch (KeyNotFoundException)
                {
                    return new ExistsResult
                    {
                        Exists = false
                    };
                }
            }
        }

        #endregion

        #region Upsert

        public async Task<IMutationResult> UpsertAsync<T>(string id, T content, UpsertOptions options)
        {
            var transcoder = options.Transcoder ?? _transcoder;
            using (var upsertOp = new Set<T>
            {
                Key = id,
                Content = content,
                Cas = options.Cas,
                Cid = Cid,
                Expires = options.Expiry.ToTtl(),
                DurabilityLevel = options.DurabilityLevel,
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = transcoder
            })
            {
                await ExecuteOp(upsertOp, options.Token, options.Timeout).ConfigureAwait(false);
                return new MutationResult(upsertOp.Cas, null, upsertOp.MutationToken);
            }
        }

        #endregion

        #region Insert

        public async Task<IMutationResult> InsertAsync<T>(string id, T content, InsertOptions options)
        {
            var transcoder = options.Transcoder ?? _transcoder;
            using (var insertOp = new Add<T>
            {
                Key = id,
                Content = content,
                Cas = options.Cas,
                Cid = Cid,
                Expires = options.Expiry.ToTtl(),
                DurabilityLevel = options.DurabilityLevel,
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = transcoder
            })
            {
                await ExecuteOp(insertOp, options.Token, options.Timeout).ConfigureAwait(false);
                return new MutationResult(insertOp.Cas, null, insertOp.MutationToken);
            }
        }

        #endregion

        #region Replace

        public async Task<IMutationResult> ReplaceAsync<T>(string id, T content, ReplaceOptions options)
        {
            var transcoder = options.Transcoder ?? _transcoder;

            using (var replaceOp = new Replace<T>
            {
                Key = id,
                Content = content,
                Cas = options.Cas,
                Cid = Cid,
                Expires = options.Expiry.ToTtl(),
                DurabilityLevel = options.DurabilityLevel,
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = transcoder
            })
            {
                await ExecuteOp(replaceOp, options.Token, options.Timeout).ConfigureAwait(false);
                return new MutationResult(replaceOp.Cas, null, replaceOp.MutationToken);
            }
        }

        #endregion

        #region Remove

        public async Task RemoveAsync(string id, RemoveOptions options)
        {
            using (var removeOp = new Delete
            {
                Key = id,
                Cas = options.Cas,
                Cid = Cid,
                DurabilityLevel = options.DurabilityLevel,
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(removeOp, options.Token, options.Timeout).ConfigureAwait(false);
            }
        }

        #endregion

        #region Unlock

        public async Task UnlockAsync<T>(string id, UnlockOptions options)
        {
            using (var unlockOp = new Unlock
            {
                Key = id,
                Cid = Cid,
                Cas = options.Cas,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(unlockOp, options.Token, options.Timeout).ConfigureAwait(false);
            }
        }

        #endregion

        #region Touch

        public async Task TouchAsync(string id, TimeSpan expiry, TouchOptions options)
        {
            using (var touchOp = new Touch
            {
                Key = id,
                Cid = Cid,
                Expires = expiry.ToTtl(),
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(touchOp, options.Token, options.Timeout).ConfigureAwait(false);
            }
        }

        #endregion

        #region GetAndTouch

        public async Task<IGetResult> GetAndTouchAsync(string id, TimeSpan expiry, GetAndTouchOptions options)
        {
            var transcoder = options.Transcoder ?? _transcoder;

            using (var getAndTouchOp = new GetT<byte[]>
            {
                Key = id,
                Cid = Cid,
                Expires = expiry.ToTtl(),
                DurabilityTimeout = TimeSpan.FromMilliseconds(1500),
                Transcoder = transcoder
            })
            {
                await ExecuteOp(getAndTouchOp, options.Token, options.Timeout);
                return new GetResult(getAndTouchOp.ExtractData(), transcoder);
            }
        }

        #endregion

        #region GetAndLock

        public async Task<IGetResult> GetAndLockAsync(string id, TimeSpan expiry, GetAndLockOptions options)
        {
            var transcoder = options.Transcoder ?? _transcoder;

            using (var getAndLockOp = new GetL<byte[]>
            {
                Key = id,
                Cid = Cid,
                Expiry = expiry.ToTtl(),
                Transcoder = transcoder
            })
            {
                await ExecuteOp(getAndLockOp, options.Token, options.Timeout);
                return new GetResult(getAndLockOp.ExtractData(), transcoder);
            }
        }

        #endregion

        #region LookupIn

        public async Task<ILookupInResult> LookupInAsync(string id, IEnumerable<OperationSpec> specs, LookupInOptions options)
        {
            using (var lookup = await ExecuteLookupIn(id, specs, options))
            {
                return new LookupInResult(lookup.ExtractData(), lookup.Cas, null);
            }
        }

        private async Task<MultiLookup<byte[]>> ExecuteLookupIn(string id, IEnumerable<OperationSpec> specs, LookupInOptions options)
        {
            // convert new style specs into old style builder
            var builder = new LookupInBuilder<byte[]>(null, null, id, specs);

            //add the virtual xttar attribute to get the doc expiration time
            if (options.Expiry)
            {
                builder.Get(VirtualXttrs.DocExpiryTime, SubdocPathFlags.Xattr);
            }

            var lookup = new MultiLookup<byte[]>
            {
                Key = id,
                Builder = builder,
                Cid = Cid,
                Transcoder = _transcoder
            };

            await ExecuteOp(lookup, options.Token, options.Timeout);
            return lookup;
        }

        #endregion

        #region MutateIn

        public async Task<IMutationResult> MutateInAsync(string id, IEnumerable<OperationSpec> specs, MutateInOptions options)
        {
            // convert new style specs into old style builder
            var builder = new MutateInBuilder<byte[]>(null, null, id, specs);

            using (var mutation = new MultiMutation<byte[]>
            {
                Key = id,
                Builder = builder,
                Cid = Cid,
                DurabilityLevel = options.DurabilityLevel,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(mutation, options.Token, options.Timeout);
                return new MutationResult(mutation.Cas, null, mutation.MutationToken);
            }
        }

        #endregion

        #region Append

        public async Task<IMutationResult> AppendAsync(string id, byte[] value, AppendOptions options)
        {
            using (var op = new Append<byte[]>
            {
                Cid = Cid,
                Key = id,
                Content = value,
                DurabilityLevel = options.DurabilityLevel,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(op, options.Token, options.Timeout);
                return new MutationResult(op.Cas, null, op.MutationToken);
            }
        }

        #endregion

        #region Prepend

        public async Task<IMutationResult> PrependAsync(string id, byte[] value, PrependOptions options)
        {
            using (var op = new Prepend<byte[]>
            {
                Cid = Cid,
                Key = id,
                Content = value,
                DurabilityLevel = options.DurabilityLevel,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(op, options.Token, options.Timeout);
                return new MutationResult(op.Cas, null, op.MutationToken);
            }
        }

        #endregion

        #region Increment

        public async Task<ICounterResult> IncrementAsync(string id, IncrementOptions options)
        {
            using (var op = new Increment
            {
                Cid = Cid,
                Key = id,
                Delta = options.Delta,
                Initial = options.Initial,
                DurabilityLevel = options.DurabilityLevel,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(op, options.Token, options.Timeout);
                return new CounterResult(op.GetValue(), op.Cas, null, op.MutationToken);
            }
        }

        #endregion

        #region Decrement

        public async Task<ICounterResult> DecrementAsync(string id, DecrementOptions options)
        {
            using (var op = new Decrement
            {
                Cid = Cid,
                Key = id,
                Delta = options.Delta,
                Initial = options.Initial,
                DurabilityLevel = options.DurabilityLevel,
                Transcoder = _transcoder
            })
            {
                await ExecuteOp(op, options.Token, options.Timeout);
                return new CounterResult(op.GetValue(), op.Cas, null, op.MutationToken);
            }
        }

        #endregion

        #region GetAnyReplica / GetAllReplicas

        public async Task<IGetReplicaResult> GetAnyReplicaAsync(string id, GetAnyReplicaOptions options)
        {
            var vBucket = (VBucket) _bucket.KeyMapper.MapKey(id);
            if (!vBucket.HasReplicas)
            {
                Log.LogWarning($"Call to GetAnyReplica for key [{id}] but none are configured. Only the active document will be retrieved.");
            }

            var tasks = new List<Task<IGetReplicaResult>>(vBucket.Replicas.Length + 1);

            var transcoder = options.Transcoder ?? _transcoder;

            // get primary
            tasks.Add(GetPrimary(id, options.CancellationToken, transcoder));

            // get replicas
            tasks.AddRange(vBucket.Replicas.Select(index => GetReplica(id, index, options.CancellationToken, transcoder)));

            return await Task.WhenAny(tasks).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public IEnumerable<Task<IGetReplicaResult>> GetAllReplicasAsync(string id, GetAllReplicasOptions options)
        {
            var vBucket = (VBucket) _bucket.KeyMapper.MapKey(id);
            if (!vBucket.HasReplicas)
            {
                Log.LogWarning($"Call to GetAllReplicas for key [{id}] but none are configured. Only the active document will be retrieved.");
            }

            var tasks = new List<Task<IGetReplicaResult>>(vBucket.Replicas.Length + 1);

            var transcoder = options.Transcoder ?? _transcoder;

            // get primary
            tasks.Add(GetPrimary(id, options.CancellationToken, transcoder));

            // get replicas
            tasks.AddRange(vBucket.Replicas.Select(index => GetReplica(id, index, options.CancellationToken, transcoder)));

            return tasks;
        }

        private async Task<IGetReplicaResult> GetPrimary(string id, CancellationToken cancellationToken, ITypeTranscoder transcoder)
        {
            using (var getOp = new Get<object>
            {
                Key = id,
                Cid = Cid,
                Transcoder = transcoder
            })
            {
                await ExecuteOp(getOp, cancellationToken).ConfigureAwait(false);
                return new GetReplicaResult(getOp.ExtractData(), transcoder)
                {
                    Id = getOp.Key,
                    Cas = getOp.Cas,
                    OpCode = getOp.OpCode,
                    Flags = getOp.Flags,
                    Header = getOp.Header,
                    IsMaster = true
                };
            }
        }

        private async Task<IGetReplicaResult> GetReplica(string id, short index, CancellationToken cancellationToken, ITypeTranscoder transcoder)
        {
            using (var getOp = new ReplicaRead<object>
            {
                Key = id,
                Cid = Cid,
                VBucketId = index,
                Transcoder = transcoder
            })
            {
                await ExecuteOp(getOp, cancellationToken).ConfigureAwait(false);
                return new GetReplicaResult(getOp.ExtractData(), transcoder)
                {
                    Id = getOp.Key,
                    Cas = getOp.Cas,
                    OpCode = getOp.OpCode,
                    Flags = getOp.Flags,
                    Header = getOp.Header,
                    IsMaster = false
                };
            }
        }

        #endregion
    }
}