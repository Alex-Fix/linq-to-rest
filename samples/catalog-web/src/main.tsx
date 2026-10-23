import { App as AntApp, ConfigProvider } from "antd";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
import { App } from "./App";
import { SessionProvider } from "./session";
import "./styles.css";

const root = document.getElementById("root");
if (root === null) {
  throw new Error("index.html needs a #root element.");
}

createRoot(root).render(
  <StrictMode>
    <ConfigProvider theme={{ token: { colorPrimary: "#4f46e5", borderRadius: 8 } }}>
      <AntApp>
        <SessionProvider>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </SessionProvider>
      </AntApp>
    </ConfigProvider>
  </StrictMode>,
);
