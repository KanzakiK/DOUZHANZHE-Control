import { useState, useEffect, useCallback, useRef } from "react";

const LS_KEY = "douzhanzhe_card_order";

const DEFAULT_ORDER = [
  "cpu-monitor",
  "gpu-monitor",
  "fan-info",
  "mem-disk",
  "cpu-adjust",
  "gpu-adjust",
  "keyboard-light",
  "current-strategy",
  "system-switches",
  "about",
];

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

export function useCardOrder() {
  const [order, setOrder] = useState(loadOrder);

  useEffect(() => {
    localStorage.setItem(LS_KEY, JSON.stringify(order));
  }, [order]);

  const moveCard = useCallback((from, to) => {
    setOrder((prev) => {
      const next = [...prev];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });
  }, []);

  const resetOrder = useCallback(() => {
    setOrder(DEFAULT_ORDER);
  }, []);

  return { order, setOrder, moveCard, resetOrder };
}
