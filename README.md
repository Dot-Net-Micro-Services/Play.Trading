# Play.Trading
Trading Microservice

## Build the docker image
```powershell
$version=1.0.2
$env:GH_OWNER="Dot-Net-Micro-Services"
$env:GH_PAT="[PAT HERE]"
docker build --secret id=GH_OWNER --secret id=GH_PAT -t play.trading:$version
```

## Run the docker image
```powershell
docker run -it -rm -p 5006:5006 --name trading play.trading:$version .
```