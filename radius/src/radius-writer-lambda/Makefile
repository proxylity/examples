build-RadiusWriterFunction:
	dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/radius-writer-lambda $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
