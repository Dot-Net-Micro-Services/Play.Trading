# Play.Trading
Trading Microservice

## Build the docker image
```powershell
$version="1.0.1"
$env:GH_OWNER="Dot-Net-Micro-Services"
$env:GH_PAT="[PAT HERE]"
docker build --secret id=GH_OWNER --secret id=GH_PAT -t play.trading:$version .
```

## Run the docker image
```powershell
$cosmosDbConnectionString="[CONNECTION STRING HERE]"
$serviceBusConnectionString="[CONNECTION STRING HERE]"
docker run -it --rm -p 5006:5006 --name trading
-e MongoDbSettings__ConnectionString=$cosmosDbConnectionString
-e ServiceBusSettings__ConnectionString=$serviceBusConnectionString
-e ServiceSettings__MessageBroker="SERVICEBUS" play.trading:$version
```