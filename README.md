Reacto-KinesiX
=======================

A [Rx](https://rx.codeplex.com/)-based .Net client library for [Amazon Kinesis](http://aws.amazon.com/kinesis/).


## The Basics

#### Before you start

Please familiarize yourself with how **Amazon Kinesis** works by looking through its online [documentations](http://aws.amazon.com/documentation/kinesis/), in particular its [Key Concepts](http://docs.aws.amazon.com/kinesis/latest/dev/key-concepts.html) and [Limitations](http://docs.aws.amazon.com/kinesis/latest/dev/service-sizes-and-limits.html).


#### Getting Started

This library enables you to create a client application which consumes and processes records that have been pushed to an *Amazon Kinesis* **stream** by taking care of most of the plumbing involved.

To process incoming records, you need to provide an implementation for the `IRecordProcessor` interface which has the following methods:

<table>
	<tbody>
		<tr>
			<td><strong>Process</strong></td>
			<td><p>Process a <i>record</i> received from the <i>Stream</i>.</p></td>
		</tr>
		<tr>
			<td><strong>GetErrorHandlingMode</strong></td>
			<td><p>If the processor failed to processor a record due to unhanded exception, this method will be invoked to give you the chance to decide how the error should be handled. 
				</p>There are two available error handling modes:</p>
				<ul>
					<li>Retry n times and then <strong>skip</strong> to the next record</li>
					<li>Retry n times and then <strong>stop</strong> processing further records from this <i>shard</i></li>
				</ul>
				<p>In both cases, if the number of retry attempts is reached and the record still cannot be processed then the <i>OnMaxRetryExceeded</i> method below will be invoked to give you one last chance to deal with the failing <i>record</i> before we either move onto the next <i>record</i> or stop processing the <i>shard</i> altogether.				
			</td>
		</tr>
		<tr>
			<td><strong>OnMaxRetryExceeded</strong></td>
			<td><p>Last chance to deal with a failing <i>record</i> when the number of retry attempts specified by the <i>GetErrorHandlingMode</i> method above has been reached.</p>
				<p>For example, you might choose to:</p>
				<ul>
					<li>save the data in the <i>record</i> onto <i>Amazon SQS</i> for processing later</li>
					<li>send out notification via <i>Amazon SNS</i>
					<li>...
				</ul>
			</td>
		</tr>
	</tbody>
</table>

To start, you can create a client application by calling the static method `ReactoKinesixApp.CreateNew` which returns a running instance of `IReactoKinesixApp` that will start processing *records* from the *stream* straight away!

#### Tracking the state of your client application

To enable us to track the state of your client application (e.g. what *shards* are we processing and where in the stream of *records* did we get to (a checkpoint so that we can easily return to where we stopped at at a later time), the client application uses a *Amazon DynamoDB* table (such as the following) to store the necessary state information for each client application.
![Example state table](http://i.imgur.com/J3Mfh0n.png)

#### Assigning Worker IDs

Each node (e.g. *EC2* instance running the client application) that is processing records from a *stream* should be given a unique *worker ID* to identify itself. If you're running your client application within *Amazon EC2*, then *Instance ID* is a perfect choice to act as a meaningful *worker ID*.

#### F# Example

```fsharp

let awsKey      = "AKIAI5Y767DTOFBUSYAA"
let awsSecret   = "zollLGekGcjIdFvCzvtbyf9OfCI1R3nvjtkSQgSM"
let region      = RegionEndpoint.USEast1
let appName		= "TestApp"
let streamName	= "TestStream"

let act (record : Record) =
    let msg = System.Encoding.UTF8.GetString(record.Data)
    printfn """

=================================================
=================================================
=================================================
%s : %s
=================================================
=================================================
=================================================

"""         record.SequenceNumber msg  

let processor = { new IRecordProcessor with 
                    member this.Process record = act record
                    member this.GetErrorHandlingMode _ = RetryAndStop 3
                    member this.OnMaxRetryExceeded (record, mode) = maxRetryExceeded record mode }

let app = ReactoKinesixApp.CreateNew(awsKey, awsSecret, region, appName, streamName, "PHANTOM", processor)
```


## Features

#### Stopping and Starting processing of a shard

If for some reason you need to stop processing a *shard*, and restart it later, you can call the `StopProcessing`and `StartProcessing` methods on a running `IReactoKinesixApp` instance.

> **Note**: when stopping processing of a *shard*, in order to avoid lost of progress and potentially process the same *records* more than once when processing is resumed, processing of the *shard* will come to a stop only after we have managed to finish processing the current batch of *recrods* that have been received and that the checkpoint has been updated successfully in *Amazon DynamoDB*.  

> **Note**: if you are running the client application on multiple nodes then you'll need to call the stop/start processing method on all the nodes otherwise another node will simply take over processing of the *shard* when the heartbeat timeout has lapsed.

#### Changing processor on the fly

You can also change the `IRecordProcessor` implementation used by the client application at runtime, by calling the `ChangeProcessor` method on a running `IReactoKinesixApp` instance and the change will take effect straight away.

> **Note**: if you are running the client application on multiple nodes then you'll need to call the `ChangeProcessor` method on all the nodes.

#### Stopping the client application

To completely stop the client application and release all the resources currently used, simply **dispose** of the running `IReactoKinesixApp` instance. Doing so will stop the processing of all the shards whilst still making sure that the application is kept in a consistent state so that we are able to resume later from where we left off without risk processing the same *records* again.


#### Handling shard merge/split

When you [merge](http://docs.aws.amazon.com/kinesis/latest/APIReference/API_MergeShards.html) or [split](http://docs.aws.amazon.com/kinesis/latest/APIReference/API_SplitShard.html) *shards* in *Amazon Kinesis*, it will create new *shard(s)* and the old *shards* will be closed (but still available to pull records from for 1 day). When this happens the client application will see the new *shards* and start processing them as soon as they become available, and the old *shards* will be processed until all their records have been processed. **This is handled automatically** by this library.

#### Configuring the client application

Whilst you don't need to specify a configuration when creating a new client application using the `ReactoKinesixApp.CreateNew` static method, a **default configuration is used** with the following settings:

<table>
	<thead>
		<tr>
			<td><strong>Configuration</strong></td>
			<td><strong>Default Value</strong></td>
			<td><strong>Description</strong></td>
		</tr>
	</thead>
	<tbody>
		<tr>
			<td>DynamoDBReadThroughput</td>
			<td>10</td>
			<td>Read throughput to use for the DynamoDB table.</td>
		</tr>
		<tr>
			<td>DynamoDBWriteThroughput</td>
			<td>10</td>
			<td>Write throughput to use for the DynamoDB table.</td>
		</tr>
		<tr>
			<td>DynamoDBTableSuffix</td>
			<td>KinesisState</td>
			<td>Suffix used to name your application's state table in DynamoDB.</td>
		</tr>
		<tr>
			<td>Heartbeat</td>
			<td>30 seconds</td>
			<td>Heartheat frequency.</td>
		</tr>
		<tr>
			<td>HeartbeatTimeout</td>
			<td>3 minutes</td>
			<td>Timeout for the heartbeat check. Default is 3 minutes.</td>
		</tr>
		<tr>
			<td>EmptyReceiveDelay</td>
			<td>3 seconds</td>
			<td>Delay in trying to pull the stream if the last pull returned no records.</td>
		</tr>
		<tr>
			<td>MaxDynamoDBRetries</td>
			<td>3</td>
			<td>Maximum number of retries on DynamoDB operations.</td>
		</tr>
		<tr>
			<td>MaxKinesisRetries</td>
			<td>3</td>
			<td>Maximum number of retries on Kinesis operations.</td>
		</tr>
		<tr>
			<td>CheckStreamChangesFrequency</td>
			<td>1 minute</td>
			<td>How frequently should we check for shard merges/splits in the stream.</td>
		</tr>
		<tr>
			<td>CheckUnprocessedShardsFrequency</td>
			<td>1 minute</td>
			<td>How frequently should we check for shards whose worker has died.</td>
		</tr>
		<tr>
			<td>LoadBalanceFrequency</td>
			<td>3 minutes</td>
			<td>How frequently should we try to balance the load amongst the workers.</td>
		</tr>
		<tr>
			<td>HandoverRequestExpiry</td>
			<td>10 minutes</td>
			<td>How much time to allow a handover request to complete.</td>
		</tr>
		<tr>
			<td>CheckPendingHandoverRequestFrequency</td>
			<td>1 minute</td>
			<td>How frequently should we check for pending handover requests for a shard.</td>
		</tr>
	</tbody>
</table> 

If you need to use a different configuration to the default, then simply create an instance of `ReactoKinesixConfig` with the configurations you want and pass it into the `ReactoKinesixApp.CreateNew` when creating your client application.

> **Note**: if you know that your application will use a **large number** of *shards* and worker nodes then you will want to **increase the read and write throughput** for the *DynamoDB* table otherwise database operations are likely to be throttled on a regular basis and **cause delays in processing your records**.


#### Distributed processing

As you scale up the *stream* by adding more shards to it, you will need to increase the processing capabilities   


## Error Handling

As mentioned in the [**Getting Started**](#getting-started) section of this guide, the `IRecordProcessor` interface requires you to implement these three methods:
- `Process`
- `GetErrorHandlingMode`
- `OnMaxRetryExceeded`

if an error is thrown by your implementation of `IRecordProcessor.Process` when processing a record then the library will call the `IRecordProcessor.GetErrorHandlingMode` method to give you the opportunity to decide how to handle the exception for this particular record.

You can choose to retry the record a number of times and then either 
- skip the record
- stop processing this shard altogether 

if the specified retries have been reached and the error still persists then the library will proceed to call the `IRecordProcessor.OnMaxRetryExceeded` method to give you a last chance to handle the *record* before we skip to the next *record* or stop processing the *shard*. 

> **Note**: you may want to ensure that the data carried by the failing *record* is not lost by implementing a mechanism to fall back to *Amazon SQS* in your implementation of `IRecordProcessor.OnMaxRetryExceeded`. 
> 
> Once captured in *SQS* the data can be processed by another process and potentially retried for up to 14 days (*SQS*'s max retention period) although in practice if the data cannot be processed with so many attempts you probably want to send out an alert and have an engineer look into it! 

> **Note**: depending on the data carried by the *record* you may choose to adopt a different error handling mode (number of retries and whether to skip or stop) depending on how important it is for you to process the data sequentially. 
> 
> You can adopt this strategy by inspecting the data carried by the *record* in your implementation of `IRecordProcessor.GetErrorHandlingMode` and returning a different error handling mode depending on the data.

#### tl;dr

If this is too much of a long winded explanation, then the following flowchart should give you a pretty good idea of how the error handling flow works: 

![](http://i.imgur.com/Zv4cjuC.png)

#### When to use *RetryAndStop*?

> **Note**: *Amazon Kinesis* uses the *partition key* (which you supply when you push the *record* to *Kinesis*) to determine which 

When it's absolutely paramount for you to preserve the order in which records for a particular *partition key* is processed. For example, all analytic events for a player in a social game will have the same *partition key* and will therefore end up in the same shard and if these events must be processed sequentially then you will want to use the *RetryAndStop* error handing mode to ensure that persistent/temporary errors does not cause the events to be processed out-of-order.

> **Note**: if processing of a shard is stopped due to the use of the **RetryAndStop** handling mode then the client application will not try to process this shard again unless explicitly told to do with when you call the `IReactoKinesixApp.StartProcessing` method. 
> 
> However, other workers/nodes will still take over processing of this shard, but if the problem that is causing the *record* to fail is not local to the earlier node, then each and every node that attempts to process the shard will also fail and eventually they will all stop trying to process this particular shard.

