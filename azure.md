
```bash
az login
```

```bash
az containerapp up \
--name tupleserver \
--resource-group my-tuple-rg \
--location eastus \
--source . \
--ingress external \
--target-port 8080 \
--transport tcp
```