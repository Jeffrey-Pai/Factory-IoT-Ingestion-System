import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');

// Load test configuration
export const options = {
  vus: 50,                    // 50 Virtual Users
  duration: '5m',             // Run for 5 minutes
  
  thresholds: {
    http_req_duration: ['p(95)<200'],  // 95th percentile < 200ms
    errors: ['rate<0.01'],              // Error rate < 1%
  },
};

// Machine IDs to test (simulating multiple machines)
const machineIds = [
  'M001', 'M002', 'M003', 'M004', 'M005',
  'M010', 'M015', 'M020', 'M025', 'M030',
];

export default function () {
  // Randomly select a machine ID
  const machineId = machineIds[Math.floor(Math.random() * machineIds.length)];
  
  // API endpoint
  const url = `http://localhost:8080/api/v1/telemetry/${machineId}/latest?count=10`;
  
  // Make the GET request
  const response = http.get(url);
  
  // Verify response
  const success = check(response, {
    'status is 200': (r) => r.status === 200,
    'response has body': (r) => r.body && r.body.length > 0,
    'response time < 200ms': (r) => r.timings.duration < 200,
  });
  
  // Track errors
  errorRate.add(!success);
  
  // Think time between requests (0.5-1.5 seconds)
  sleep(Math.random() + 0.5);
}
