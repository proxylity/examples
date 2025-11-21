cmake_minimum_required(VERSION 3.16)
project(Torus LANGUAGES CXX)

set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Use C++17 instead of C++20
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Tell CMake where to find the installed aws-lambda-runtime
set(CMAKE_PREFIX_PATH /tmp/lambda)

# Find the aws-lambda-runtime package
find_package(aws-lambda-runtime REQUIRED)

# Create executable
add_executable(bootstrap main.cpp)

# Include directories
target_include_directories(bootstrap PRIVATE
    .
)

# Link against the aws-lambda-runtime library
target_link_libraries(bootstrap PRIVATE AWS::aws-lambda-runtime)

# Package the Lambda function for deployment
aws_lambda_package_target(bootstrap)