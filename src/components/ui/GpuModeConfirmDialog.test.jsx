import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import GpuModeConfirmDialog from "./GpuModeConfirmDialog";

afterEach(cleanup);

function renderDialog(overrides = {}) {
  const props = {
    open: true,
    currentLabel: "混合",
    targetLabel: "集显",
    externalDisplayRisk: true,
    busy: false,
    error: "",
    onCancel: vi.fn(),
    onConfirm: vi.fn(),
    onRestart: vi.fn(),
    ...overrides,
  };
  render(<GpuModeConfirmDialog {...props} />);
  return props;
}

describe("GpuModeConfirmDialog", () => {
  it("展示当前模式、目标模式和集显外接显示风险", () => {
    renderDialog();

    expect(screen.getByRole("dialog", { name: "确认切换 GPU 模式" })).toBeTruthy();
    expect(screen.getByText("混合")).toBeTruthy();
    expect(screen.getByText("集显")).toBeTruthy();
    expect(screen.getByText("集显模式可能暂时中断外接显示输出")).toBeTruthy();
  });

  it("三个按钮分别触发取消、确认和立即重启", () => {
    const props = renderDialog();

    fireEvent.click(screen.getByRole("button", { name: "取消" }));
    fireEvent.click(screen.getByRole("button", { name: "确认切换" }));
    fireEvent.click(screen.getByRole("button", { name: "立即重启" }));

    expect(props.onCancel).toHaveBeenCalledOnce();
    expect(props.onConfirm).toHaveBeenCalledOnce();
    expect(props.onRestart).toHaveBeenCalledOnce();
  });

  it("处理中禁用所有按钮", () => {
    renderDialog({ busy: true });

    expect(screen.getByRole("button", { name: "取消" }).disabled).toBe(true);
    expect(screen.getByRole("button", { name: "正在切换…" }).disabled).toBe(true);
    expect(screen.getByRole("button", { name: "正在处理…" }).disabled).toBe(true);
  });
});
