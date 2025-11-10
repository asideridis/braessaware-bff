import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '10s', target: 20 },
    { duration: '30s', target: 20 },
    { duration: '10s', target: 0 }
  ],
  thresholds: {
    http_req_duration: ['p(95)<500']
  }
};

const plannerOn = __ENV.BRAESS_ENABLED === 'true';
const baseUrl = __ENV.BFF_BASE_URL || 'http://localhost:5000';

export default function () {
  const res = http.get(`${baseUrl}/api/dashboard`, {
    headers: {
      'x-braess-test': plannerOn ? 'on' : 'off'
    }
  });
  check(res, {
    'status is 200': (r) => r.status === 200,
  });
  sleep(0.1);
}
