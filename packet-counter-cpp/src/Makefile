.PHONY: build-PacketCounterLambda clean

build-PacketCounterLambda:
	docker build -t lambda-builder .
	echo $(PWD)
	docker run --rm --entrypoint="" -v $(PWD)/src:/build lambda-builder make build-in-container

	mkdir -p $(ARTIFACTS_DIR)

	mv build/bootstrap $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap

build-in-container: build-dependencies

	rm -rf build/

	cmake -S . -B build/
	cmake --build build/

build-dependencies:
	# Setup vendor directory
	rm -rf vendor/ build/
	mkdir -p vendor/cppcodec vendor/nlohmann vendor/aws-lambda-cpp
	mkdir -p /tmp/lambda

	# Install nlohmann/json and cppcodec libraries into vendor directory
	wget -qO /tmp/include.zip https://github.com/nlohmann/json/releases/download/v3.12.0/include.zip && \
		unzip -jq /tmp/include.zip "single_include/nlohmann/json.hpp" -d vendor/nlohmann && rm /tmp/include.zip

	wget -qO /tmp/include.zip https://github.com/tplgy/cppcodec/archive/refs/tags/v0.2.zip && \
		unzip -q /tmp/include.zip "cppcodec-0.2/cppcodec/*" -d vendor && rm /tmp/include.zip && \
		mv vendor/cppcodec-0.2/cppcodec/* vendor/cppcodec/ && rm -rf vendor/cppcodec-0.2

	# Download aws-lambda-cpp
	wget -qO /tmp/awslambda.zip https://github.com/awslabs/aws-lambda-cpp/archive/refs/tags/v0.2.10.zip
	unzip -q /tmp/awslambda.zip -d vendor/aws-lambda-cpp

	# Build and install
	cmake -S vendor/aws-lambda-cpp/aws-lambda-cpp-0.2.10/ -B build/ \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_INSTALL_PREFIX=/tmp/lambda
	cmake --build build/ -j$(nproc)
	cmake --install build/

clean:
	rm -rf vendor/ build/ build-app/ /tmp/lambda
