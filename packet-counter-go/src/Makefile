build-PacketCounterLambda:
	go mod tidy
	go mod vendor
	GOOS=linux GOARCH=amd64 go build -o bootstrap main.go
	mkdir -p $(ARTIFACTS_DIR)
	mv bootstrap $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
