import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { useRef, useLayoutEffect, useState } from "react";

export default function SortableCard({ id, children, editMode, onHide }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  const wrapperRef = useRef(null);
  const [lockedHeight, setLockedHeight] = useState(null);

  // 首次渲染后延迟锁定高度，防止异步数据加载导致 CSS columns 重新平衡
  useLayoutEffect(() => {
    const timer = setTimeout(() => {
      if (wrapperRef.current) {
        setLockedHeight(wrapperRef.current.offsetHeight);
      }
    }, 800);
    return () => clearTimeout(timer);
  }, []);

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    position: "relative",
    breakInside: "avoid",
    minHeight: lockedHeight ? `${lockedHeight}px` : undefined,
  };

  return (
    <div ref={(node) => { setNodeRef(node); wrapperRef.current = node; }} style={style} className={isDragging ? "z-50" : ""}>
      {editMode && (
        <>
          <button
            {...attributes}
            {...listeners}
            className="absolute top-2 right-8 z-10 cursor-grab active:cursor-grabbing w-7 h-7 flex items-center justify-center rounded-lg text-xs font-bold"
            style={{ background: "var(--primary-2)", color: "#fff", border: "1px solid var(--border)" }}
            title="拖拽排序"
          >
            ⠿
          </button>
          {onHide && (
            <button
              onClick={onHide}
              className="absolute top-2 right-1 z-10 w-7 h-7 flex items-center justify-center rounded-lg text-xs font-bold"
              style={{ background: "var(--warn)", color: "#fff", border: "1px solid var(--border)" }}
              title="隐藏此模块"
            >
              ✕
            </button>
          )}
        </>
      )}
      {children}
    </div>
  );
}
