# Load Testing

This directory contains a k6 script (`dashboard.js`) that exercises the `/api/dashboard` endpoint with the planner toggled on/off.

## Prerequisites

* [k6](https://k6.io/docs/getting-started/installation/)

## Running

1. Start the downstream sample services and the BFF (see project README for full instructions).
2. Run the baseline test with the planner disabled and export the metrics to CSV:

   ```bash
   BRAESS_ENABLED=false k6 run --out csv=results/planner-off.csv dashboard.js
   ```

3. Run the test with the planner enabled:

   ```bash
   BRAESS_ENABLED=true k6 run --out csv=results/planner-on.csv dashboard.js
   ```

4. Compare the CSV outputs (`results/planner-off.csv` vs `results/planner-on.csv`) to evaluate p95 improvements.
