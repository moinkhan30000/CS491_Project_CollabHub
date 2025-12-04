/**
 * PluginManager
 * Handles loading, registering, and managing plugins
 */

const fs = require('fs').promises;
const path = require('path');
const { v4: uuidv4 } = require('uuid');

class PluginManager {
  constructor() {
    // Store all registered plugins
    this.plugins = new Map();
    
    // Store plugin metadata
    this.pluginMetadata = new Map();
    
    // Plugin directory
    this.pluginDir = path.join(__dirname, '..', 'plugins');
    
    // Event listeners for plugin lifecycle
    this.listeners = {
      'plugin:loaded': [],
      'plugin:unloaded': [],
      'plugin:error': []
    };
  }

  /**
   * Load all plugins from the plugins directory
   */
  async loadPlugins() {
    try {
      // Ensure plugin directory exists
      await fs.mkdir(this.pluginDir, { recursive: true });
      
      const files = await fs.readdir(this.pluginDir);
      
      for (const file of files) {
        if (file.endsWith('.js')) {
          await this.loadPlugin(file);
        }
      }
      
      return Array.from(this.plugins.values());
    } catch (error) {
      console.error('Error loading plugins:', error);
      throw error;
    }
  }

  /**
   * Load a single plugin
   */
  async loadPlugin(filename) {
    try {
      const pluginPath = path.join(this.pluginDir, filename);
      
      // Clear require cache to allow hot-reloading
      delete require.cache[require.resolve(pluginPath)];
      
      const pluginModule = require(pluginPath);
      
      // Validate plugin structure
      if (!pluginModule.name || !pluginModule.version) {
        throw new Error(`Invalid plugin structure: ${filename}`);
      }
      
      // Generate unique ID if not provided
      const pluginId = pluginModule.id || uuidv4();
      
      // Create plugin entry
      const plugin = {
        id: pluginId,
        name: pluginModule.name,
        version: pluginModule.version,
        description: pluginModule.description || '',
        author: pluginModule.author || 'Unknown',
        enabled: true,
        loadedAt: new Date().toISOString(),
        
        // Plugin hooks and methods
        hooks: pluginModule.hooks || {},
        api: pluginModule.api || {},
        routes: pluginModule.routes || [],
        
        // Frontend configuration
        frontend: {
          component: pluginModule.frontend?.component || null,
          icon: pluginModule.frontend?.icon || null,
          position: pluginModule.frontend?.position || 'sidebar',
          styles: pluginModule.frontend?.styles || {}
        }
      };
      
      // Register plugin
      this.plugins.set(pluginId, plugin);
      this.pluginMetadata.set(pluginId, {
        filename,
        path: pluginPath
      });
      
      // Execute initialization hook if exists
      if (pluginModule.hooks?.onInit) {
        await pluginModule.hooks.onInit();
      }
      
      // Emit loaded event
      this.emit('plugin:loaded', plugin);
      
      console.log(`✅ Loaded plugin: ${plugin.name} v${plugin.version}`);
      
      return plugin;
    } catch (error) {
      console.error(`❌ Failed to load plugin ${filename}:`, error.message);
      this.emit('plugin:error', { filename, error: error.message });
      throw error;
    }
  }

  /**
   * Unload a plugin
   */
  async unloadPlugin(pluginId) {
    const plugin = this.plugins.get(pluginId);
    
    if (!plugin) {
      throw new Error(`Plugin not found: ${pluginId}`);
    }
    
    // Execute cleanup hook if exists
    const metadata = this.pluginMetadata.get(pluginId);
    if (metadata) {
      const pluginModule = require(metadata.path);
      if (pluginModule.hooks?.onDestroy) {
        await pluginModule.hooks.onDestroy();
      }
    }
    
    // Remove from registry
    this.plugins.delete(pluginId);
    this.pluginMetadata.delete(pluginId);
    
    // Emit unloaded event
    this.emit('plugin:unloaded', plugin);
    
    console.log(`🔌 Unloaded plugin: ${plugin.name}`);
    
    return true;
  }

  /**
   * Get all plugins
   */
  getAllPlugins() {
    return Array.from(this.plugins.values());
  }

  /**
   * Get a specific plugin
   */
  getPlugin(pluginId) {
    return this.plugins.get(pluginId);
  }

  /**
   * Get plugin count
   */
  getPluginCount() {
    return this.plugins.size;
  }

  /**
   * Enable/disable a plugin
   */
  togglePlugin(pluginId, enabled) {
    const plugin = this.plugins.get(pluginId);
    
    if (!plugin) {
      throw new Error(`Plugin not found: ${pluginId}`);
    }
    
    plugin.enabled = enabled;
    return plugin;
  }

  /**
   * Execute a plugin hook
   */
  async executeHook(hookName, ...args) {
    const results = [];
    
    for (const [pluginId, plugin] of this.plugins) {
      if (plugin.enabled && plugin.hooks[hookName]) {
        try {
          const result = await plugin.hooks[hookName](...args);
          results.push({ pluginId, result });
        } catch (error) {
          console.error(`Error executing hook ${hookName} for plugin ${plugin.name}:`, error);
          this.emit('plugin:error', { pluginId, hook: hookName, error: error.message });
        }
      }
    }
    
    return results;
  }

  /**
   * Event system
   */
  on(event, callback) {
    if (this.listeners[event]) {
      this.listeners[event].push(callback);
    }
  }

  emit(event, data) {
    if (this.listeners[event]) {
      this.listeners[event].forEach(callback => callback(data));
    }
  }

  /**
   * Get plugins formatted for frontend
   */
  getFrontendPlugins() {
    return this.getAllPlugins().map(plugin => ({
      id: plugin.id,
      name: plugin.name,
      version: plugin.version,
      description: plugin.description,
      author: plugin.author,
      enabled: plugin.enabled,
      frontend: plugin.frontend
    }));
  }
}

module.exports = PluginManager;
