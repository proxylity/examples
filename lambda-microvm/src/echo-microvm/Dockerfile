FROM node:24-alpine

WORKDIR /app
COPY app.js .

# 8080 receives packet batches from Proxylity; 9000 receives Lambda lifecycle hooks.
EXPOSE 8080 9000

# Lambda snapshots the application once it is running.
CMD ["node", "app.js"]
