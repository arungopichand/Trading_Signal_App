import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 30,
  duration: '5m',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<800'],
  },
};

const symbols = ['AAPL','MSFT','NVDA','TSLA','AMD','META','AMZN','GOOGL','SPY','QQQ','PLTR','NFLX'];
const baseUrl = __ENV.BASE_URL || 'http://localhost:10000';

export default function () {
  const symbol = symbols[Math.floor(Math.random() * symbols.length)];
  const res = http.get(`${baseUrl}/api/market/unified/${symbol}`, { timeout: '15s' });

  check(res, {
    'status is 200': (r) => r.status === 200,
    'has quote': (r) => {
      try {
        const body = JSON.parse(r.body);
        return !!body && !!body.quote && body.quote.currentPrice > 0;
      } catch (e) {
        return false;
      }
    },
  });

  sleep(0.12);
}
