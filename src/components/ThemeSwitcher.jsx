import { themes } from "../data/themes";

export default function ThemeSwitcher({ currentTheme, onThemeChange }) {
  return (
    <select
      value={currentTheme}
      onChange={(e) => onThemeChange(e.target.value)}
      className="w-full rounded-xl text-sm px-3 py-2"
      style={{
        background: "var(--card-2)",
        color: "var(--text)",
        border: "1px solid var(--border)",
        outline: "none",
      }}
    >
      {themes.map((theme) => (
        <option key={theme.id} value={theme.id}>
          {theme.name}
        </option>
      ))}
    </select>
  );
}
