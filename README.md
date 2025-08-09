# Time Store DB

A simple distributed database with SQLite backend for storing and retrieving time entries. This project incorporates Raft consensus algorithm for handling leader election and log replication.
Currently, it only supports a rigid schema for time entries and doesn't provide any customizability.

```json
{
  "id": "string (GUID, auto generates if not provided)",
  "deviceId": "string",
  "timestamp": "date-time",
  "value": "double"
}
```
The OpenAPI specification for the DB API can be found in [`Here`](https://time-store.readme.io/reference/post_raft-vote). The **Raft** endpoints are used by the _RaftService_ and shouldn't be used by the user except 'status' endpoint.
The user should interact with the database using the **TimeSeries** endpoints.

## Getting Started

1. First clone the repository in your local machine:
```bash
   git clone https://github.com/fffffatah/time-store.git
```
2. Run the below command to build the docker images and start the services. Make sure you have Docker and .NET 9 SDK installed in your local machine:
```bash
   docker compose up -d
```
3. Once the containers are up and running, you can access the API documentation at `http://localhost:5001/swagger/index.html`. After that, you can use the endpoints like you do with any traditional REST API.

The docker-compose file initiates a 3 node cluster of the database and elects a leader on the first run. The leader gets re-elected if the current leader goes down.

## Features
All the features of the database are explained below based on each endpoints. The CRUD operations are handled using EF Core. 

### RaftController

1. [POST] raft/vote

This is used by the RaftService to request votes from its peers (other nodes). Only candidates can request for votes and followers become candidate by incrementing the term value
when it doesn't hear heartbeat from the leader node for 100ms (3 times). The other candidates decide whether to vote for the requesting candidate or not by comparing the term value with its own term value. If the requesting candidate's term value is greater than its own, it votes for the candidate.
Also, it takes last log index and last log term into account to decide whether to vote for the candidate or not. If the candidate's last log index is greater than its own, it votes for the candidate.

Request:

```json
{
  "term": "int64",
  "candidateId": "string",
  "lastLogIndex": "int64",
  "lastLogTerm": "int64"
}
```

Response:

```json
{
  "term": "int64",
  "voteGranted": "boolean"
}
```

2. [POST] raft/append

This is used by the RaftService to append entries to the log. The leader node sends this request to its followers to replicate the log entries.
The followers append the entries to their log and return an acknowledgment.

Request:

```json
{
  "term": "int64",
  "leaderId": "string",
  "prevLogIndex": "int64",
  "prevLogTerm": "int64",
  "entries": [
    {
      "term": "int64",
      "command": "string (serialized JSON of the time entry)"
    }
  ],
  "leaderCommit": "int64"
}
```

Response:

```json
{
  "term": "int64",
  "success": "boolean"
}
```

3. [GET] raft/status

This is used to get the status of the RaftService. It returns the current state (Candidate, Leader, Follower), the ID of the leader node, and the ID of the current node.

Response:

```json
{
  "state": "string (Candidate, Leader, Follower) | enum",
  "leaderId": "string",
  "nodeId": "string"
}
```

### TimeSeriesController

1. [GET] api/timeseries

This endpoint is used to retrieve all time entries from the database. It returns a list of time entries.

Response:

```json
[
  {
    "id": "string (GUID)",
    "deviceId": "string",
    "timestamp": "date-time",
    "value": "double"
  }
]
```

2. [POST] api/timeseries

This endpoint is used to create a new time entry in the database. It accepts a time entry object and returns the created time entry.

Request:

```json
{
  "id": "string (GUID, optional)",
  "deviceId": "string",
  "timestamp": "date-time",
  "value": "double"
}
```

Response:

```json
{
  "id": "string (GUID)",
  "deviceId": "string",
  "timestamp": "date-time",
  "value": "double"
}
```

3. [GET] api/timeseries/device/{deviceId}

This endpoint is used to retrieve all time entries for a specific device. It accepts a device ID as a path parameter and returns a list of time entries for that device.

Response:

```json
[
  {
    "id": "string (GUID)",
    "deviceId": "string",
    "timestamp": "date-time",
    "value": "double"
  }
]
```

## Limitations & Future Work

**Schema**: To provide flexible schema for need based customizability, a column can be added to the _Data_ table to store a serialized JSON object.
This will allow users to store any custom model alongside the actual time series entry.

**Data Partitioning**: The time series data can be partitioned into multiple tables based on the device ID or timestamp (time bucketing based on hours).
This will allow for better query performance when querying over a time range or for a specific device.

Also, RaftService related tables (RaftLog and RaftState) can be moved to a separate SQLite file to avoid DB locks during write operations.

**Flexible Query**: The current implementation only supports querying by device ID and fetching all time entries. Future work can include adding support for querying by time range, value range, and other custom queries.
Additionally, auto background aggregation mechanism can be added to aggregate data across specific time spans.