import { useState, useEffect, useCallback, useRef } from "react";

const LS_KEY = "douzhanzhe_card_order";
const LS_HIDDEN_KEY = "douzhanzhe_hidden_cards";
const API_UI_STATE = "/api/ui-state";

const DEFAULT_ORDER = [
  "cpu-monitor",
  "gpu-monitor",
  "cpu-adjust",
  "gpu-adjust",
  "mem-disk",
  "fan-info",
  "keyboard-light",
  "gpu-mode",
  "about",
  "system-switches",
];
const DEFAULT_HIDDEN = ["system-switches"];

function loadHidden() {
  try {
    const raw = localStorage.getItem(LS_HIDDEN_KEY);
    if (raw) return new Set(JSON.parse(raw));
  } catch {}
  return new Set();
}

function loadOrder() {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed) && parsed.length > 0) {
        // 合入 DEFAULT_ORDER 中有但已保存排序中缺失的新卡片（追加到末尾）
        const existing = new Set(parsed);
        const missing = DEFAULT_ORDER.filter((id) => !existing.has(id));
        if (missing.length > 0) return [...parsed, ...missing];
        return parsed;
      }
    }
  } catch {}
  return DEFAULT_ORDER;
}

export function useCardOrder(onSyncResult) {
  const [order, setOrder] = useState(loadOrder);
  const [hiddenCards, setHiddenCards] = useState(loadHidden);
  const [loadedFromServer, setLoadedFromServer] = useState(false);
  const onSyncRef = useRef(onSyncResult);
  onSyncRef.current = onSyncResult;

  // 启动时从服务端加载已保存的 UI 状态
  useEffect(() => {
    fetch(API_UI_STATE)
      .then((r) => r.json())
      .then((data) => {
        if (data && Array.isArray(data.cardOrder) && data.cardOrder.length > 0) {
          const existing = new Set(data.cardOrder);
          const missing = DEFAULT_ORDER.filter((id) => !existing.has(id));
          setOrder(missing.length > 0 ? [...data.cardOrder, ...missing] : data.cardOrder);
          setHiddenCards(new Set(data.hiddenCards || []));
        }
      })
      .catch(() => {})
      .finally(() => setLoadedFromServer(true));
  }, []);

  // 持久化到 localStorage
  useEffect(() => {
    localStorage.setItem(LS_KEY, JSON.stringify(order));
  }, [order]);

  useEffect(() => {
    localStorage.setItem(LS_HIDDEN_KEY, JSON.stringify([...hiddenCards]));
  }, [hiddenCards]);

  // 同步到服务端（退出编辑时触发）
  const syncToServer = useCallback(() => {
    const payload = { cardOrder: [...order], hiddenCards: [...hiddenCards] };
    fetch(API_UI_STATE, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    })
      .then((r) => {
        if (r.ok) onSyncRef.current?.(true);
        else onSyncRef.current?.(false);
      })
      .catch(() => onSyncRef.current?.(false));
  }, [order, hiddenCards]);

  const moveCard = useCallback((from, to) => {
    setOrder((prev) => {
      const next = [...prev];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });
  }, []);

  const toggleHidden = useCallback((id) => {
    setHiddenCards((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const showAll = useCallback(() => {
    setHiddenCards(new Set());
  }, []);

  const visibleCards = order.filter((id) => !hiddenCards.has(id));
  const hiddenList = order.filter((id) => hiddenCards.has(id));

  const resetOrder = useCallback(() => {
    setOrder([...DEFAULT_ORDER]);
    setHiddenCards(new Set(DEFAULT_HIDDEN));
  }, []);

  return {
    order, setOrder, moveCard, resetOrder, syncToServer,
    hiddenCards, toggleHidden, showAll, visibleCards, hiddenList,
  };
}
