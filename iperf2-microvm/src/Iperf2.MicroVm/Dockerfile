# The MicroVM image is built from this file plus one native executable (scripts/publish.sh puts both
# in build/image/). NativeAOT means the image needs no .NET runtime: just the C library.
#
# al2023-minimal is Lambda's own minimal container base for MicroVMs. It is snapshot-compatible, and its
# glibc (2.34) is the version the executable is linked against. scripts/publish.sh checks that.
FROM public.ecr.aws/lambda/microvms:al2023-minimal

COPY iperf2-microvm /app/iperf2-microvm

# 8080 receives packet batches from Proxylity; 9000 receives Lambda lifecycle hooks.
EXPOSE 8080 9000

# Lambda snapshots the application once it is running and /ready returns 200.
ENTRYPOINT ["/app/iperf2-microvm"]
