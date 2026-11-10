import http from 'node:http';
import { createApp, configFromEnv } from './app.js';
import { services } from './services.js';

export function parseEndpoint(value = 'localhost:3000') {
  const parsed = new URL(`http://${value}`);
  if (parsed.username || parsed.password || parsed.pathname !== '/' || parsed.search || parsed.hash || !parsed.port) throw Error('Invalid AWS_LAMBDA_HTTP_ENDPOINT');
  return { host: parsed.hostname.replace(/^\[|\]$/g, ''), port: Number(parsed.port) };
}

const config = configFromEnv(process.env);
const endpoint = parseEndpoint(process.env.AWS_LAMBDA_HTTP_ENDPOINT || undefined);
http.createServer(createApp(config, services(config))).listen(endpoint.port, endpoint.host);
