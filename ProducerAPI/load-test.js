import http from "k6/http";
import { check } from "k6";

export const options = {
  scenarios: {
    high_rps_test: {
      executor: "constant-arrival-rate",
      rate: 10000, // 💥 2000 requests per second
      timeUnit: "1s", // per second
      duration: "10s",
      preAllocatedVUs: 50, // K6 will spin up to this many VUs initially
      maxVUs: 200, // Increase if needed for load
    },
  },
};

export default function () {
  const url = "http://producer-api:8080/api/publish";

  const payload = JSON.stringify({
    CameraId: "cam-1",
    Action: "start",
  });

  const headers = { "Content-Type": "application/json" };

  const res = http.post(url, payload, { headers });

  check(res, {
    "✅ status is 200": (r) => r.status === 200,
  });

  if (res.status !== 200) {
    console.log(`❌ Status: ${res.status}, Body: ${res.body}`);
  }
}

// import http from "k6/http";
// import { check } from "k6";

// export const options = {
//   scenarios: {
//     high_rps_test: {
//       executor: "constant-arrival-rate",
//       rate: 3000, // 💥 2000 requests per second
//       timeUnit: "1s", // per second
//       duration: "20s",
//       preAllocatedVUs: 50, // K6 will spin up to this many VUs initially
//       maxVUs: 200, // Increase if needed for load
//     },
//   },
// };

// function camera1EventSend() {
//   const url = "http://producer-service:8080/Producer/send";

//   const payload = JSON.stringify({
//     CameraId: "cam-1",
//     Action: "start",
//   });

//   const headers = { "Content-Type": "application/json" };

//   const res = http.post(url, payload, { headers });

//   check(res, {
//     "✅ status is 200": (r) => r.status === 200,
//   });

//   if (res.status !== 200) {
//     console.log(`❌ Status: ${res.status}, Body: ${res.body}`);
//   }
// }

// function camera3EventSend() {
//   const url = "http://producer-service:8080/Producer/send";

//   const payload = JSON.stringify({
//     CameraId: "cam-3",
//     Action: "running",
//   });

//   const headers = { "Content-Type": "application/json" };

//   const res = http.post(url, payload, { headers });

//   check(res, {
//     "✅ status is 200": (r) => r.status === 200,
//   });

//   if (res.status !== 200) {
//     console.log(`❌ Status: ${res.status}, Body: ${res.body}`);
//   }
// }

// export default function () {
//   // Randomly choose between camera 1 and camera 3 events
//   const randomChoice =
//     Math.random() < 0.5 ? camera1EventSend : camera3EventSend;
//   // Call the chosen function
//   randomChoice();
// }
