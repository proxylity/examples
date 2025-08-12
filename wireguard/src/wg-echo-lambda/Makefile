build-Lambda:
	dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/wg-echo-lambda $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
