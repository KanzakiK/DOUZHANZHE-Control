import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import PerCoreCoPanel from "./PerCoreCoPanel";

vi.mock("../../services/uxtuAdapter", () => ({
  fetchPerCoreCo: vi.fn(),
  applyPerCoreCo: vi.fn(),
  probePerCoreCo: vi.fn(),
}));

import { fetchPerCoreCo, applyPerCoreCo } from "../../services/uxtuAdapter";

const topology = { ccdCount: 2, coresPerCcd: 8 };
const limits = { minMv: -30, maxMv: 0 };

function renderPanel(overrides = {}) {
  const props = {
    settings: { mode: "cfg-silent" },
    overrides: {},
    saveOverride: vi.fn(),
    clearOverride: vi.fn().mockResolvedValue({ ok: true }),
    switching: false,
    ...overrides,
  };
  return render(<PerCoreCoPanel {...props} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  fetchPerCoreCo.mockResolvedValue({
    ok: true,
    supported: true,
    topology,
    limits,
    perCoreCo: [],
  });
});

afterEach(cleanup);

describe("PerCoreCoPanel", () => {
  it("默认关闭，只有打开开关后才展示完整的 16 核编辑区", async () => {
    renderPanel();

    await waitFor(() => expect(fetchPerCoreCo).toHaveBeenCalledOnce());
    expect(screen.getByText("未启用")).toBeTruthy();
    expect(screen.queryAllByRole("slider")).toHaveLength(0);
    expect(applyPerCoreCo).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "打开逐核心降压" }));

    expect(screen.getByText("待应用")).toBeTruthy();
    expect(screen.getAllByRole("slider")).toHaveLength(17);
    expect(screen.getAllByText(/^核心 \d+$/)).toHaveLength(16);
    expect(applyPerCoreCo).not.toHaveBeenCalled();
  });

  it("已保存逐核配置时直接显示为已启用并展开全部核心", async () => {
    const saved = Array.from({ length: 16 }, (_, i) => ({
      ccd: Math.floor(i / 8),
      core: i % 8,
      mv: i % 2 === 0 ? -5 : -9,
    }));
    fetchPerCoreCo.mockResolvedValue({
      ok: true,
      supported: true,
      topology,
      limits,
      perCoreCo: saved,
    });

    renderPanel({
      overrides: { perCoreCo: saved },
    });

    await waitFor(() => expect(fetchPerCoreCo).toHaveBeenCalledOnce());
    expect(screen.getByText("已启用")).toBeTruthy();
    expect(screen.getByRole("button", { name: "关闭逐核心降压" })).toBeTruthy();
    expect(screen.getAllByRole("slider")).toHaveLength(17);
    expect(screen.getAllByText(/^核心 \d+$/)).toHaveLength(16);
  });
});
