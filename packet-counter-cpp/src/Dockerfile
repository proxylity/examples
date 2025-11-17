FROM public.ecr.aws/lambda/provided:al2

# Install build tools
RUN yum install -y \
    gcc-c++ \
    make \
    cmake3 \
    libcurl-devel \
    git \
    tar \
    zip \
    unzip \
    wget

# Create symbolic link for cmake
RUN ln -s /usr/bin/cmake3 /usr/bin/cmake

WORKDIR /build
