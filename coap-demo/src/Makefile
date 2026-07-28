# Shared Makefile for the two Native AOT (provided.al2023) functions.
#
# Both RequestHandlerFunction and AsyncResponderFunction set CodeUri to this
# directory (../src/) rather than their own subfolder, because SAM's
# CustomMakeBuilder (Metadata.BuildMethod: makefile) only copies the CodeUri
# tree into a temp build directory before invoking `make` there. Since both
# functions have a ProjectReference to the sibling CoapDemo.Common project,
# CodeUri has to be their common parent so that project comes along too.

build-RequestHandlerFunction:
	cd RequestHandler && dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv RequestHandler/publish/RequestHandler $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap

build-AsyncResponderFunction:
	cd AsyncResponder && dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv AsyncResponder/publish/AsyncResponder $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
