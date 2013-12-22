﻿namespace ReactoKinesix

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reactive
open System.Reactive.Linq
open System.Threading

open log4net

open Amazon
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open Amazon.Kinesis
open Amazon.Kinesis.Model

open ReactoKinesix.Model
open ReactoKinesix.Utils

type IRecordProcessor = 
    abstract member Process : Record -> unit

type internal ReactoKinesix (kinesis    : IAmazonKinesis,
                             dynamoDB   : IAmazonDynamoDB,
                             config     : ReactoKinesixConfig,
                             tableName  : TableName, 
                             streamName : StreamName,
                             workerId   : WorkerId,
                             shardId    : ShardId,
                             processor  : IRecordProcessor) as this =
    let loggerName  = sprintf "ReactorKinesixWorker[Stream:%O, Worker:%O, Shard:%O]" streamName workerId shardId
    let logger      = LogManager.GetLogger(loggerName)
    let logDebug    = logDebug logger
    let logWarn     = logWarn  logger
    let logError    = logError logger

    do logDebug "Starting worker..." [||]

    let batchReceivedEvent          = new Event<Iterator * Record seq>() // when a new batch of records have been received
    let recordProcessedEvent        = new Event<Record>()                // when a record has been processed by processor
    let processErroredEvent         = new Event<Record * Exception>()    // when an error was caught when processing a record
    let batchProcessedEvent         = new Event<int * Iterator>()        // when a batch has finished processing with the iterator for next batch

    let initializedEvent            = new Event<unit>()           // when the worker has been initialized
    let initializationFailedEvent   = new Event<Exception>()      // when an error was caught whist initializing the worker
    
    let emptyReceiveEvent           = new Event<unit>()           // the worker received no records from the stream
    let checkpointEvent             = new Event<SequenceNumber>() // when the latest checkpoint is updated in state table
    let heartbeatEvent              = new Event<unit>()           // when a heartbeat is recorded
    let conditionalCheckFailedEvent = new Event<unit>()           // when a conditional check failure was encountered when writing to the state table
    
    let disposeInvoked = ref 0
    let dispose () = (this :> IDisposable).Dispose()

    let cts = new CancellationTokenSource();

    let updateHeartbeat _ = 
        let work = async {
            let! res = DynamoDBUtils.updateHeartbeat dynamoDB tableName workerId shardId |> Async.Catch
            match res with
            | Choice1Of2 () -> heartbeatEvent.Trigger()
            | Choice2Of2 ex -> match ex with 
                               | :? ConditionalCheckFailedException -> 
                                    conditionalCheckFailedEvent.Trigger()
                                    dispose()
                               | _ -> // TODO : what's the right thing to do here?
                                      // a) give up, let the next cycle (or next checkpoint update) update the heartbeat
                                      // b) retry a few times
                                      // c) retry until we succeed
                                      // for now, try option a) as it's not entirely critical for one heartbeat update to
                                      // succeed, if problem is with DynamoDB and it persists then eventually we'll be
                                      // blocked on the checkpoint update too and either succeed eventualy or some other
                                      // worker will take over if they were able to successful write to DynamoDB instead
                                      // of the current worker
                                      logWarn "Failed to update heartbeat, ignoring..." [||]
                                      ()
        }
        Async.Start(work, cts.Token)

    let rec updateCheckpoint seqNum =
        let work = async {
            let! res = DynamoDBUtils.updateCheckpoint seqNum dynamoDB tableName workerId shardId |> Async.Catch
            match res with
            | Choice1Of2 () -> checkpointEvent.Trigger(seqNum)
            | Choice2Of2 ex -> match ex with 
                               | :? ConditionalCheckFailedException -> 
                                    conditionalCheckFailedEvent.Trigger()
                                    dispose()
                               | exn -> 
                                      // TODO : what's the right thing to do here if we failed to update checkpoint? 
                                      // a) keep going and risk allowing more records to be processed multiple times
                                      // b) crash and let the last batch of records be processed against
                                      // c) wait and recurse until we succeed until some other worker takes over
                                      //    processing of the shard in which case we get conditional check failed
                                      // for now, try option c) with a 1 second delay as the risk of processing the same
                                      // records can ony be determined by the consumer, perhaps expose some configurable
                                      // behaviour under these circumstances?
                                      logError exn "Failed to update checkpoint to [{0}]...retrying" [| seqNum |]
                                      do! Async.Sleep(1000)
                                      updateCheckpoint seqNum
        }
        Async.Start(work, cts.Token)

    let fetchNextRecords iterator = 
        async {
            logDebug "Fetching next records with iterator [{0}]" [| iterator |]

            let! nextIterator, batch = KinesisUtils.getRecords kinesis streamName shardId iterator
            batchReceivedEvent.Trigger(IteratorToken nextIterator, batch)
        }

    let processRecord record = 
        try 
            processor.Process(record)
            recordProcessedEvent.Trigger(record)
            Success(SequenceNumber record.SequenceNumber)
        with
        | ex -> 
            processErroredEvent.Trigger(record, ex)
            Failure(SequenceNumber record.SequenceNumber, ex)

    let stopProcessing       = conditionalCheckFailedEvent.Publish
    let stopProcessingLogSub = stopProcessing.Subscribe(fun _ -> logDebug "Stop processing..." [||])
                         
    let heartbeat        = Observable.Interval(config.Heartbeat).TakeUntil(stopProcessing)
    let heartbeatLogSub  = heartbeat.Subscribe(fun _ -> logDebug "Sending heartbeat..." [||])
    let heartbeatSub     = heartbeat.Subscribe(updateHeartbeat)

    let checkpointLogSub = checkpointEvent.Publish.Subscribe(fun seqNum -> logDebug "Updating sequence number checkpoint [{0}]" [| seqNum |])
    
    let nextBatch       = initializedEvent.Publish                            
                            .Merge(Observable.Delay(emptyReceiveEvent.Publish, config.EmptyReceiveDelay))
                            .Merge(checkpointEvent.Publish.Select(fun _ -> ()))

    let fetch           = batchProcessedEvent.Publish.TakeUntil(stopProcessing)
    let fetchSub        = fetch.Subscribe(fun (_, iterator) -> fetchNextRecords iterator |> Async.StartImmediate)

    let received        = batchReceivedEvent.Publish
    let receivedLogSub  = received.Subscribe(fun (iterator, records) -> 
                            logDebug "Received batch of [{1}] records, next iterator [{0}]" [| iterator; Seq.length records|])

    let processing      = received
                            .Zip(nextBatch, fun receivedBatch _ -> receivedBatch)
                            .TakeUntil(stopProcessing)
    let processingLogSub = processing.Subscribe(fun (iterator, records) -> logDebug "Start processing batch of [{1}] records, next iterator [{0}]" [| iterator; Seq.length records |])
    let processingSub   = 
        processing.Subscribe(fun (iterator, records) ->
            match Seq.isEmpty records with
            | true -> 
                emptyReceiveEvent.Trigger()
                batchProcessedEvent.Trigger(0, iterator)
            | _ -> 
                let count, lastResult = 
                    records 
                    |> Seq.scan (fun (count, _) record -> 
                        let res = processRecord record
                        (count + 1, Some res)) (0, None)
                    |> Seq.takeWhile (fun (_, res) -> match res with | Some (Failure _) -> false | _ -> true)
                    |> Seq.reduce (fun _ lastRes -> lastRes)

                match lastResult with
                | Some (Success seqNum)      -> 
                    logDebug "Batch was fully processed [{0}], last sequence number [{1}]" [| count; seqNum |]

                    updateCheckpoint(seqNum)
                    batchProcessedEvent.Trigger(count, iterator)
                | Some (Failure (seqNum, _)) -> 
                    logWarn "Batch was partially processed [{0}/{1}], last successful sequence number [{2}]" 
                            [| count; Seq.length records; seqNum |]

                    updateCheckpoint(seqNum)
                    batchProcessedEvent.Trigger(count, NoIteratorToken <| AtSequenceNumber seqNum))

    let processedLogSub = recordProcessedEvent.Publish.Subscribe(fun (record : Record) ->
                            logDebug "Processed record [PartitionKey:{0}, SequenceNumber:{1}]"
                                     [| record.PartitionKey; record.SequenceNumber |])

    // this is only initialization sequence
    let init = 
        async {
            let! createShardResult = DynamoDBUtils.createShard dynamoDB tableName workerId shardId
            match createShardResult with
            | Failure exn -> initializationFailedEvent.Trigger(exn)
            | Success _   -> 
                let! getShardStatusResult = DynamoDBUtils.getShardStatus dynamoDB config tableName shardId
                match getShardStatusResult with
                | Failure exn -> initializationFailedEvent.Trigger(exn)
                | Success status -> 
                    match status with
                    | New(workerId', _) when workerId' = workerId -> 
                        // the shard has not been processed before, so start from the oldest record
                        fetchNextRecords (NoIteratorToken <| TrimHorizon) |> Async.StartImmediate
                        initializedEvent.Trigger()
                    | NotProcessing(_, _, seqNum) -> 
                        // the shard has not been processed currently, start from the last checkpoint
                        fetchNextRecords (NoIteratorToken <| AfterSequenceNumber seqNum) |> Async.StartImmediate
                        initializedEvent.Trigger()
                    | Processing(workerId', seqNum) when workerId' = workerId -> 
                        // the shard was being processed by this worker, continue from where we left off
                        fetchNextRecords (NoIteratorToken <| AfterSequenceNumber seqNum) |> Async.StartImmediate
                        initializedEvent.Trigger()
                    | _ -> () // TODO : what to do here? Some other worker's working on this shard, so do we wait or die?
        }

    // keep retrying failed initializations until it succeeds
    let _ = initializationFailedEvent.Publish
                .TakeUntil(initializedEvent.Publish)
                .Subscribe(fun _ -> Async.Start init)

    do Async.Start init

    let cleanup (disposing : bool) =
        // ensure that resources are only disposed of once
        if System.Threading.Interlocked.CompareExchange(disposeInvoked, 1, 0) = 0 then
            logDebug "Disposing..." [||]

            cts.Cancel()

            [| stopProcessingLogSub; heartbeatLogSub; heartbeatSub; checkpointLogSub; fetchSub;
                receivedLogSub; processingLogSub; processingSub; processedLogSub; (cts :> IDisposable) |]
            |> Array.iter (fun x -> x.Dispose())

            logDebug "Disposed." [||]

    interface IDisposable with
        member this.Dispose () = 
            GC.SuppressFinalize(this)
            cleanup(true)

    // provide a finalizer so that in the case the consumer forgets to dispose of the worker the
    // finalizer will clean up
    override this.Finalize () =
        logWarn "Finalizer is invoked. Please ensure that the object is disposed in a deterministic manner instead." [||]
        cleanup(false)

type ReactoKinesixApp (awsKey     : string, 
                       awsSecret  : string, 
                       region     : RegionEndpoint,
                       appName    : string,
                       streamName : string,
                       workerId   : string,
                       processor  : IRecordProcessor,
                       ?config    : ReactoKinesixConfig) =
    // track a static dictionary of application names that are currenty running to prevent
    // consumer from accidentally starting multiple apps with same name
    static let runningApps = new ConcurrentDictionary<string, string>()        
    do if not <| runningApps.TryAdd(appName, streamName) 
       then raise <| AppNameIsAlreadyRunning streamName

    let config = defaultArg config <| new ReactoKinesixConfig()
    do Utils.validateConfig config

    let loggerName = sprintf "ReactoKinesixApp[AppName:%s, Stream:%O]" appName streamName
    let logger     = LogManager.GetLogger(loggerName)
    let logDebug   = logDebug logger
    let logInfo    = logInfo  logger
    let logWarn    = logWarn  logger

    let cts = new CancellationTokenSource()

    let stateTableReadyEvent = new Event<string>()

    let kinesis    = AWSClientFactory.CreateAmazonKinesisClient(awsKey, awsSecret, region)
    let dynamoDB   = AWSClientFactory.CreateAmazonDynamoDBClient(awsKey, awsSecret, region)    
    let streamName, workerId = StreamName streamName, WorkerId workerId

    let initResult = DynamoDBUtils.initStateTable dynamoDB config appName |> Async.RunSynchronously
    let tableName  = match initResult with 
                     | Success tableName -> tableName 
                     | Failure(_, exn)   -> raise <| InitializationFailed exn

    let _ = Observable.FromAsync(DynamoDBUtils.awaitStateTableReady dynamoDB tableName)
                      .Subscribe(fun _ -> stateTableReadyEvent.Trigger(tableName.ToString())
                                          logDebug "State table [{0}] is ready" [| tableName |])

    // this is a mutable dictionary of workers but can only be mutated from within the controller agent
    // which is single threaded by nature so there's no need for placing locks around add/remove operations
    let knownShards = new HashSet<ShardId>()
    let workers = new Dictionary<ShardId, ReactoKinesix>()
    let body (inbox : Agent<ControlMessage>) = 
        async {
            while true do
                let! msg = inbox.Receive()

                match msg with
                | StartWorker(shardId, reply) ->
                    match workers.TryGetValue(shardId) with
                    | true, worker -> reply.Reply()
                    | _ -> let worker = new ReactoKinesix(kinesis, dynamoDB, config, tableName, streamName, workerId, shardId, processor)
                           workers.Add(shardId, worker)
                           reply.Reply()
                | StopWorker(shardId, reply) -> 
                    match workers.TryGetValue(shardId) with
                    | true, worker -> 
                        (worker :> IDisposable).Dispose()
                        workers.Remove(shardId) |> ignore
                        reply.Reply()                        
                    | _ -> reply.Reply()
                | AddKnownShard(shardId, reply) ->
                    knownShards.Add(shardId) |> ignore
                    reply.Reply()
                | RemoveKnownShard(shardId, reply) ->
                    knownShards.Remove(shardId) |> ignore
                    reply.Reply()
        }
    let controller = Agent<ControlMessage>.StartProtected(body, cts.Token, onRestart = fun exn -> logWarn "Controller agent was restarted due to exception :\n {0}" [| exn |])

    let startWorker shardId   = controller.PostAndAsyncReply(fun reply -> StartWorker(shardId, reply))
    let stopWorker  shardId   = controller.PostAndAsyncReply(fun reply -> StopWorker(shardId, reply))
    let addKnownShard shardId = controller.PostAndAsyncReply(fun reply -> AddKnownShard(shardId, reply))
    let rmvKnownShard shardId = controller.PostAndAsyncReply(fun reply -> RemoveKnownShard(shardId, reply))

    let updateWorkers (shardIds : string seq) (update : ShardId -> Async<unit>) = 
        async {
            do! shardIds                
                |> Seq.map (fun shardId -> update (ShardId shardId))
                |> Async.Parallel
                |> Async.Ignore
        }

    let refresh =
        async {
            // find difference between the shards in the stream and the shards we're currenty processing
            let! shards  = KinesisUtils.getShards kinesis streamName
            let shardIds = shards |> Seq.map (fun shard -> shard.ShardId) |> Set.ofSeq

            let knownShards = knownShards |> Seq.map (fun (ShardId shardId) -> shardId) |> Set.ofSeq
            let newShards     = Set.difference shardIds knownShards
            let removedShards = Set.difference knownShards shardIds

            if newShards.Count > 0 then
                let logArgs : obj[] = [| newShards.Count; String.Join(",", newShards) |]
                logInfo "Add [{0}] shards to known shards : [{1}]" logArgs
                do! updateWorkers newShards addKnownShard

                logInfo "Starting workers for [{0}] shards : [{1}]" logArgs
                do! updateWorkers newShards startWorker

            if removedShards.Count > 0 then
                let logArgs : obj[] = [| removedShards.Count; String.Join(",", removedShards) |]
                logInfo "Remove [{0}] shards from known shards : [{1}]" logArgs
                do! updateWorkers newShards rmvKnownShard

                logInfo "Stopping workers for [{0}] shards : [{1}]" logArgs
                do! updateWorkers newShards stopWorker
       }
    do Async.Start refresh

    let refreshSub = Observable.Interval(config.CheckStreamChangesFrequency)
                        .Subscribe(fun _ -> Async.Start refresh)
    
    let disposeInvoked = ref 0
    let cleanup (disposing : bool) =
        // ensure that resources are only disposed of once
        if System.Threading.Interlocked.CompareExchange(disposeInvoked, 1, 0) = 0 then
            logDebug "Disposing..." [||]

            refreshSub.Dispose()

            workers.Keys 
            |> Seq.toArray 
            |> Seq.map (fun shardId -> stopWorker shardId)
            |> Async.Parallel
            |> Async.Ignore
            |> Async.RunSynchronously

            cts.Cancel()
            cts.Dispose()

            runningApps.TryRemove(appName) |> ignore

            logDebug "Disposed" [||]

    member this.StartProcessing (shardId : string) = startWorker (ShardId shardId) |> Async.StartAsPlainTask
    member this.StopProcessing  (shardId : string) = stopWorker  (ShardId shardId) |> Async.StartAsPlainTask

    interface IDisposable with
        member this.Dispose () = 
            GC.SuppressFinalize(this)
            cleanup(true)

    // provide a finalizer so that in the case the consumer forgets to dispose of the app the
    // finalizer will clean up
    override this.Finalize () =
        logWarn "Finalizer is invoked. Please ensure that the object is disposed in a deterministic manner instead." [||]
        cleanup(false)