### Time Store DB

A simple distributed database with SQLite backend for storing and retrieving time entries. This project incorporates Raft consensus algorithm for handling leader election and log replication.
Currently, it only supports a rigid schema for time entries and doesn't provide any customizability.

```json
{
  "deviceId": "string",
  "timestamp": "string",
  "value": "number"
}
```
The OpenAPI specification for the DB API can be found in [`Here`](https://time-store.readme.io/reference/post_raft-vote). The **Raft** endpoints are used by the _RaftService_ and shouldn't be used by the user.
The user should interact with the database using the **TimeSeries** endpoints.

### Getting Started

1. First clone the repository in your local machine:
```bash
   git clone https://github.com/fffffatah/time-store.git
```
2. Run the below command to build the docker images and start the services. Make sure you have Docker installed and Docker service running in your local machine:
```bash
   docker compose up -d
```
3. Once the containers are up and running, you can access the API documentation at `http://localhost:5001/swagger/index.html`. After that, you can use the endpoints like you do with any traditional REST API.

The docker-compose file initiates a 3 node cluster of the database and elects a leader on the first run. The leader gets re-elected if the current leader goes down.