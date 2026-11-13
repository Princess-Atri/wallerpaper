# Stability summary (valid autoplay run)

- Log: `D:\Desktop\wallerpaper c\stability-10min.log`
- Samples: **40** (every 15s, span ~9.8 min)
- Video: 1920x1080, 10.0s loop, source 30fps, cap 60fps
- Note: final code keeps the fps filter for 60fps output; this run used the
  no-duplicate experimental path (30fps output) to compare memory.

## Working set (MB)

- first: 161.5
- last: 182.5
- delta: +21.0
- min / mean / max: 161.5 / 177.1 / 186.5
- first-half mean -> last-half mean: 172.6 -> 181.6
- linear slope: +1.862 MB/min
- largest single upward jump: +11.1 MB

## Private bytes (MB)

- first: 268.5
- last: 286.1
- delta: +17.6
- min / mean / max: 268.5 / 282.5 / 290.3
- first-half mean -> last-half mean: 278.7 -> 286.4
- linear slope: +1.574 MB/min
- largest single upward jump: +11.3 MB

## CPU / threads / handles

- CPU total: 66.09s over 10min -> ~11.0% of one core
- Threads: 91–100
- Handles: 758–785
- Hardware decode: d3d11va 40/40
- Output FPS: 30 (no duplicate filter in this experiment)

## Verdict

- Working set slope: **+1.862 MB/min**
- Private slope: **+1.574 MB/min**
- 10-minute peak 186.5MB < 200MB, but the trend is upward; 30–60 minute
  testing is required before calling it stable.
