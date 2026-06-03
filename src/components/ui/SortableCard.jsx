import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";

export default function SortableCard({ id, children, editMode }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    position: "relative",
  };

  return (
    <div ref={setNodeRef} style={style} className={isDragging ? "z-50" : ""}>
      {editMode && (
        <button
          {...attributes}
          {...listeners}
          className="absolute top-2 right-2 z-10 cursor-grab active:cursor-grabbing w-7 h-7 flex items-center justify-center rounded-lg text-xs font-bold"
          style={{ background: "var(--primary-2)", color: "#fff", border: "1px solid var(--border)" }}
          title="拖拽排序"
        >
          ⠿
        </button>
      )}
      {children}
    </div>
  );
}
