import Card from "../ui/Card";
import Gauge from "../ui/Gauge";
import SliderRow from "../ui/SliderRow";
import Sparkline from "../ui/Sparkline";
function makeSeries(base, count = 36, jitter = 10, min = 0, max = 100) {
  return Array.from({ length: count }, (_, i) => {
    const wave = Math.sin(i / 4) * (jitter * 0.6);
    const noise = Math.cos(i * 0.9) * (jitter * 0.35);
    const value = Math.round(base + wave + noise);
    return Math.max(min, Math.min(max, value));
  });
}

export default function TelemetryPanel({ telemetry, setTelemetry, settings, setSettings, uxtuPayload, fanLargeRpmTarget, fanSmallRpmTarget, setFanLargeRpmTarget, setFanSmallRpmTarget, history }) {
  const cpuSeries = history.cpu;
  const gpuSeries = history.gpu;
  const fanPctSeries = telemetry.fanLargeMax > 0
    ? history.fan.map((v) => Math.round((v / telemetry.fanLargeMax) * 100))
    : [];

  return (
    <>

<Card title="CPU 监控" className="!p-5">
          <div className="space-y-3">
            <Gauge label="占用率" value={telemetry.cpuUsage} />
            <Gauge label="温度" value={telemetry.cpuTemp} unit="°C" color="var(--warn)" />
            <Gauge label="频率" value={telemetry.cpuFreq} unit=" GHz" color="var(--ok)" max={5.2} />
            <p className="text-sm" style={{ color: "var(--muted)" }}>核心: {telemetry.cpuCores}</p>
            <Sparkline data={cpuSeries} title="CPU 负载曲线" />
          </div>
        </Card>

<Card title="GPU 监控" className="!p-5">
          <div className="space-y-3">
            <Gauge label="占用率" value={telemetry.gpuUsage} />
            <Gauge label="温度" value={telemetry.gpuTemp} unit="°C" color="var(--warn)" />
            <Gauge label="频率" value={telemetry.gpuFreq} unit=" GHz" color="var(--primary-2)" max={3.2} />
            <p className="text-sm" style={{ color: "var(--muted)" }}>
              显存: {telemetry.gpuVramUsed ?? "?"}/{telemetry.gpuVram} GB
            </p>
            <Sparkline data={gpuSeries} title="GPU 负载曲线" color="var(--primary-2)" />
          </div>
        </Card>



<Card title="风扇信息">
          <div className="space-y-3">
            <div className="space-y-1">
              <div className="flex items-center justify-between mb-2">
                <p className="text-sm">
                  大风扇(CPU): <span className="font-bold">{telemetry.fanLargeRpm}</span> RPM
                </p>
                {telemetry.fanLargeRpm > 0 && (
                  <span
                    className="inline-flex items-center justify-center w-6 h-6"
                    style={{
                      animation: `spin ${Math.max(0.5, 3 - telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax) * 2.5)}s linear infinite`,
                    }}
                  >
                    ⊙
                  </span>
                )}
              </div>
              <SliderRow
                label="大风扇目标转速"
                value={fanLargeRpmTarget}
                min={0}
                max={telemetry.fanLargeMax}
                step={100}
                unit="RPM"
                onChange={(value) => setFanLargeRpmTarget(value)}
              />
              <Gauge
                label="大风扇负载"
                value={Math.round((telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax)) * 100)}
              />
            </div>

            <div className="space-y-1">
              <div className="flex items-center justify-between mb-2">
                <p className="text-sm">
                  小风扇(GPU): <span className="font-bold">{telemetry.fanSmallRpm}</span> RPM
                </p>
                {telemetry.fanSmallRpm > 0 && (
                  <span
                    className="inline-flex items-center justify-center w-6 h-6"
                    style={{
                      animation: `spin ${Math.max(0.5, 3 - telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax) * 2.5)}s linear infinite`,
                    }}
                  >
                    ⊙
                  </span>
                )}
              </div>
              <SliderRow
                label="小风扇目标转速"
                value={fanSmallRpmTarget}
                min={0}
                max={telemetry.fanSmallMax}
                step={100}
                unit="RPM"
                onChange={(value) => setFanSmallRpmTarget(value)}
              />
              <Gauge
                label="小风扇负载"
                value={Math.round((telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax)) * 100)}
              />
            </div>

            <Sparkline data={fanPctSeries} title="风扇负载曲线" color="var(--ok)" />
          </div>
        </Card>

    </>
  );
}
