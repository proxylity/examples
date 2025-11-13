## Deploying

Note: compiled/packaged runtimes (Go, Rust, other languages that produce a native binary or use a custom runtime) require a different template and parameter overrides than interpreted runtimes (Python, Node.js, etc.). Key differences:
- Compiled binaries must be built for the Lambda execution environment first, placed under the folder referenced by CodeUri, and the template must use the "custom" template that expects a prebuilt artifact.
- Handler/runtime values differ: interpreted runtimes use module.handler and a managed runtime (e.g. python3.11). Compiled/custom runtimes typically use the executable name (often "bootstrap") and a custom/provided runtime (e.g. provided.al2023).
- Template for compiled runtimes expects a `Makefile` which builds package into artifact dir.
- Version requirements for languages are:
    - **Go**: `1.23.9`
    - **Python**: `3.11`

### Prerequisites

- An S3 bucket for SAM to store and deploy Lambda code artifacts
- AWS credentials configured for your target region (us-west-2 in these examples)
- `sam` tool.

### Compiled runtimes (Go, custom compiled)

- Use the custom template and point CodeUri at the folder containing the source code. Set LambdaHandler to the executable name (e.g. bootstrap) and LambdaRuntime to the provided runtime:
```bash
sam build --template-file ./packet-counter.custom.template.json \
    --parameter-overrides CodeUri=./go/
```
- Deploy with matching overrides:
```bash
sam deploy \
    --stack-name packet-counter-example \
    --capabilities CAPABILITY_IAM \
    --region us-west-2 \
    --s3-bucket <bucket-name> \
    --parameter-overrides CodeUri=./go/
```

### Interpreted runtimes (Python example)
- No precompiled binary required. Use the standard template and interpreter runtime:
```bash
sam build --template-file ./packet-counter.template.json \
    --parameter-overrides CodeUri=./python LambdaHandler=app.handler LambdaRuntime=python3.11
```
- Deploy:
```bash
sam deploy \
    --stack-name packet-counter-example \
    --capabilities CAPABILITY_IAM \
    --region us-west-2 \
    --s3-bucket <bucket-name> \
    --parameter-overrides CodeUri=./python LambdaHandler=app.handler LambdaRuntime=python3.11
```

### Next Steps
Refer back to [Deployment](../readme.md).

### Summary
- Use `packet-counter.custom.template.json` for compiled/custom runtimes; ensure binary name matches LambdaHandler and runtime is set to provided.al2023 (or your custom runtime).
- Use `packet-counter.template.json` and normal `module.handler` + managed runtime for interpreted languages.
- `sam build` compiles the binary into the CodeUri folder for compiled runtimes.
