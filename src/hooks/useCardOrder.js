import { useState, useEffect, useCallback, useRef } from "react";

const LS_KEY = "douzhanzhe_card_order";
const LS_HIDDEN_KEY = "douzhanzhe_hidden_cards";
const API_CONFIG = "/api/default-config";

const DEFAULT_ORDER = [
  "cpu-monitor",
  "gpu-monitor",
  "cpu-adjust",
  "gpu-adjust",
  "mem-disk",
  "fan-info",
  "current-strategy",
  "keyboard-light",
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
      if (Array.isArray(parsed) && parsed.length > 0) return parsed;
    }
  } catch {}
  return DEFAULT_ORDER;
}

export function useCardOrder(onSyncResult) {
  const [order, setOrder] = useState(loadOrder);
  const [hiddenCards, setHiddenCards] = useState(loadHidden);
  const onSyncRef = useRef(onSyncResult);
  onSyncRef.current = onSyncResult;

  // 持久化到 localStorage
  useEffect(() => {
    localStorage.setItem(LS_KEY, JSON.stringify(order));
  }, [order]);

  useEffect(() => {
    localStorage.setItem(LS_HIDDEN_KEY, JSON.stringify([...hiddenCards]));
  }, [hiddenCards]);

  // 手动同步到服务端——只在外部调用时触发
  const syncToServer = useCallback(() => {
    const payload = { order: [...order], hidden: [...hiddenCards] };
    fetch(API_CONFIG, {
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
